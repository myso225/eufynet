using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using EufyNet.Core;
using EufyNet.P2P;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Protocol;

namespace EufyNet.Transport.Mqtt;

/// <summary>
/// MQTT transport with Home Assistant discovery. Keeps the last login, station states and pictures, and publishes them
/// again after every (re)connect, so retained topics are right even when the broker restarted.
/// </summary>
public sealed class MqttTransport : IBridgeTransport, IAsyncDisposable
{
    private readonly MqttTransportOptions _options;
    private readonly ILogger _logger;
    private readonly MqttTopics _topics;
    private readonly GuardModeLabels _labels;
    private readonly IMqttClient _client;
    private readonly Channel<BridgeCommand> _commands = Channel.CreateUnbounded<BridgeCommand>();
    private readonly ConcurrentDictionary<string, StationState> _states = new();
    private readonly ConcurrentDictionary<string, byte[]> _images = new();
    /// <summary>Station → the discovery published for it on this connection (camera list and select options).</summary>
    private readonly ConcurrentDictionary<string, string> _discovered = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private LoginState? _login;
    private int _commandId;

    public MqttTransport(MqttTransportOptions options, ILogger<MqttTransport>? logger = null)
    {
        _options = options;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _topics = new MqttTopics(options.BaseTopic);
        _labels = new GuardModeLabels(options.GuardModes);
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += e =>
        {
            OnMessage(e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString() ?? "");
            return Task.CompletedTask;
        };
    }

    public async Task PublishLoginAsync(LoginState state, CancellationToken cancellationToken)
    {
        _login = state;
        if (state.NeedsVerifyCode)
            _logger.LogWarning("Eufy wants the emailed code: publish {{\"name\":\"verify_code\",\"args\":{{\"code\":\"…\"}}}} to {Topic}", _topics.Command);
        await PublishAsync(_topics.Login, MqttMessages.Login(state), retain: true, cancellationToken);
    }

    public async Task PublishStateAsync(StationState state, CancellationToken cancellationToken)
    {
        var previous = _states.GetValueOrDefault(state.StationSn);
        _states[state.StationSn] = state;
        if (!_client.IsConnected)
            return;
        await PublishDiscoveryAsync(state, cancellationToken);
        await PublishStationAsync(state, cancellationToken);
        if (previous is not null && previous.GuardMode != state.GuardMode && state.GuardMode is not null)
            await PublishAsync(_topics.Event(state.StationSn), MqttMessages.GuardModeChanged(state, _labels), retain: false, cancellationToken);
    }

    public async Task PublishEventAsync(BridgeEvent bridgeEvent, CancellationToken cancellationToken)
    {
        var e = bridgeEvent.Event;
        if (bridgeEvent.DeviceSn is { } deviceSn)
        {
            switch (e)
            {
                case DetectionEvent:
                    await PublishAsync(_topics.Motion(deviceSn), "ON", retain: false, cancellationToken);
                    break;
                case CameraImageEvent image:
                    _images[deviceSn] = image.Jpeg;
                    await PublishAsync(_topics.Image(deviceSn), image.Jpeg, retain: true, cancellationToken);
                    break;
            }
        }
        if (e is LightSwitchEvent or StorageInfoEvent && bridgeEvent.DeviceSn is null)
            return; // raw signals without a camera; the motion/recording events carry the meaning
        await PublishAsync(_topics.Event(bridgeEvent.DeviceSn ?? e.StationSn), MqttMessages.Event(bridgeEvent, _labels), retain: false, cancellationToken);
    }

    public async Task PublishResultAsync(BridgeCommandResult result, CancellationToken cancellationToken)
    {
        if (!result.Success)
            _logger.LogWarning("{Command} failed: {Message}", result.Name, result.Message);
        await PublishAsync(_topics.Result, MqttMessages.Result(result), retain: false, cancellationToken);
        // A refused guard mode change: put the select back to the station's mode.
        if (result.Name == BridgeCommands.SetGuardMode && !result.Success)
        {
            foreach (var state in _states.Values)
                await PublishStationAsync(state, cancellationToken);
        }
    }

    /// <summary>Connects (and keeps reconnecting) to the broker while commands are read.</summary>
    public async IAsyncEnumerable<BridgeCommand> ReadCommandsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var connection = KeepConnectedAsync(cancellationToken);
        await foreach (var command in _commands.Reader.ReadAllAsync(cancellationToken))
            yield return command;
        await connection;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await PublishAsync(_topics.Status, "offline", retain: true, timeout.Token);
                await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), timeout.Token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Disconnecting from the broker: {Message}", ex.Message);
            }
        }
        _client.Dispose();
        _connectLock.Dispose();
    }

    private async Task KeepConnectedAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            if (!_client.IsConnected)
            {
                try
                {
                    await ConnectAsync(ct);
                    if (failures > 0)
                        _logger.LogInformation("Reconnected to the MQTT broker after {Failures} attempts", failures);
                    failures = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // Log the first failure and then every 5 minutes or so, not every few seconds.
                    if (failures++ % Math.Max(1, (int)(TimeSpan.FromMinutes(5) / _options.ReconnectDelay)) == 0)
                        _logger.LogWarning("MQTT broker {Host}:{Port} not reachable: {Message}", _options.Host, _options.Port, ex.Message);
                }
            }
            try
            {
                await Task.Delay(_options.ReconnectDelay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithClientId(_options.ClientId)
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithWillTopic(_topics.Status)
            .WithWillPayload("offline")
            .WithWillRetain()
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        if (!string.IsNullOrEmpty(_options.Username))
            builder.WithCredentials(_options.Username, _options.Password);

        await _connectLock.WaitAsync(ct);
        try
        {
            _discovered.Clear();
            await _client.ConnectAsync(builder.Build(), ct);
            await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(_topics.Command, MqttQualityOfServiceLevel.AtLeastOnce)
                .WithTopicFilter(_topics.GuardModeSetFilter, MqttQualityOfServiceLevel.AtLeastOnce)
                .Build(), ct);
            _logger.LogInformation("Connected to the MQTT broker {Host}:{Port}, topics under {Base}/", _options.Host, _options.Port, _options.BaseTopic);

            await PublishAsync(_topics.Status, "online", retain: true, ct);
            if (_login is { } login)
                await PublishAsync(_topics.Login, MqttMessages.Login(login), retain: true, ct);
            foreach (var state in _states.Values)
            {
                await PublishDiscoveryAsync(state, ct);
                await PublishStationAsync(state, ct);
            }
            foreach (var (deviceSn, jpeg) in _images)
                await PublishAsync(_topics.Image(deviceSn), jpeg, retain: true, ct);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task PublishDiscoveryAsync(StationState state, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.DiscoveryPrefix))
            return;
        var options = GuardModeOptions(state);
        // Republished when the cameras or the select options change.
        var key = string.Join(",", state.Cameras.Select(c => c.DeviceSn + "=" + c.Name).Append(state.ActivityWindow.ToString()).Concat(options));
        if (_discovered.TryGetValue(state.StationSn, out var known) && known == key)
            return;
        foreach (var (topic, payload) in MqttMessages.Discovery(state, _options, _topics, options))
            await PublishAsync(topic, payload, retain: true, ct);
        _discovered[state.StationSn] = key;
    }

    /// <summary>The configured labels, plus the station's current modes when they are not among them.</summary>
    private List<string> GuardModeOptions(StationState state)
    {
        var options = _labels.Options.ToList();
        foreach (var mode in new[] { state.GuardMode }.OfType<GuardMode>())
        {
            if (!options.Contains(_labels.Label(mode)))
                options.Add(_labels.Label(mode));
        }
        return options;
    }

    private async Task PublishStationAsync(StationState state, CancellationToken ct)
    {
        await PublishAsync(_topics.State(state.StationSn), MqttMessages.StationState(state, _labels), retain: true, ct);
        foreach (var camera in state.Cameras)
            await PublishAsync(_topics.State(camera.DeviceSn), MqttMessages.CameraState(camera, state), retain: true, ct);
    }

    private Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct) =>
        PublishAsync(topic, System.Text.Encoding.UTF8.GetBytes(payload), retain, ct);

    private async Task PublishAsync(string topic, byte[] payload, bool retain, CancellationToken ct)
    {
        if (!_client.IsConnected)
        {
            _logger.LogDebug("Not connected to the broker; dropped {Topic}", topic);
            return;
        }
        await _client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), ct);
    }

    internal void OnMessage(string topic, string payload)
    {
        if (topic == _topics.Command)
        {
            if (ParseCommand(payload) is { } command)
                _commands.Writer.TryWrite(command);
            else
                _logger.LogWarning("Ignoring {Topic}: expected {{\"name\":…,\"station\":…,\"args\":{{…}}}}, got {Payload}", topic, Truncate(payload));
            return;
        }

        var parts = topic.Split('/');
        if (parts.Length >= 3 && topic == _topics.GuardModeSet(parts[^3]))
        {
            var stationSn = parts[^3];
            if (_labels.Parse(payload) is { } mode)
            {
                _logger.LogInformation("Guard mode {Label} ({Mode}) for {Station} requested over MQTT", _labels.Label(mode), mode, stationSn);
                _commands.Writer.TryWrite(new BridgeCommand(NextId(), BridgeCommands.SetGuardMode, stationSn,
                    new Dictionary<string, string> { ["mode"] = mode.ToString() }));
            }
            else
            {
                _logger.LogWarning("Unknown guard mode {Payload} on {Topic}; one of {Options}", Truncate(payload), topic, string.Join(", ", _labels.Options));
            }
        }
    }

    private BridgeCommand? ParseCommand(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("name", out var name) || name.GetString() is not { Length: > 0 } commandName)
                return null;
            var id = root.TryGetProperty("id", out var i) && i.ValueKind is JsonValueKind.String or JsonValueKind.Number ? i.ToString() : NextId();
            var station = root.TryGetProperty("station", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            Dictionary<string, string>? args = null;
            if (root.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object)
                args = a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText());
            if (commandName == BridgeCommands.SetGuardMode && args?.GetValueOrDefault("mode") is { } label && _labels.Parse(label) is { } mode)
                args["mode"] = mode.ToString(); // labels work here too
            return new BridgeCommand(id, commandName, station, args);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Commands received but not yet read (tests).</summary>
    internal ChannelReader<BridgeCommand> PendingCommands => _commands.Reader;

    private string NextId() => $"mqtt-{Interlocked.Increment(ref _commandId)}";

    private static string Truncate(string value) => value.Length <= 200 ? value : value[..200] + "…";
}
