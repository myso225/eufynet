using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EufyNet.P2P;

public enum P2PConnectionMode
{
    /// <summary>Only accept private (LAN) addresses — eufy-security-ws P2P_CONNECTION_SETUP=1.</summary>
    OnlyLocal,
    /// <summary>Also ask the Eufy cloud lookup servers and take whichever answers first.</summary>
    Quickest,
}

/// <summary>What the session needs to know about a station (from the cloud station list).</summary>
/// <param name="LocalAddress">Station LAN IP; when null the local lookup is broadcast.</param>
/// <param name="AppConn">Encoded cloud lookup servers; only used in <see cref="P2PConnectionMode.Quickest"/>.</param>
public sealed record StationP2PInfo(string StationSn, string P2PDid, IPAddress? LocalAddress, string? AppConn);

public sealed class P2PSessionOptions
{
    public P2PConnectionMode Mode { get; set; } = P2PConnectionMode.OnlyLocal;
    public TimeSpan LookupTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(25);
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>Delay before the second cloud lookup variant (LOOKUP_WITH_KEY2) joins in.</summary>
    public TimeSpan Lookup2Delay { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxMissedHeartbeats { get; set; } = 10;
    public int LocalLookupPort { get; set; } = P2PPackets.LocalLookupPort;
    /// <summary>Returns the station's current DSK key for cloud lookups (Quickest mode only).</summary>
    public Func<CancellationToken, Task<string?>>? DskKeyProvider { get; set; }
}

public enum P2PSessionState
{
    Idle,
    Lookup,
    Connecting,
    Connected,
    Closed,
}

/// <summary>
/// One UDP P2P connection to a station: lookup → CHECK_CAM → CAM_ID → heartbeat, ACKing every DATA packet.
/// Single use: after <see cref="Completion"/> finishes, create a new session to reconnect.
/// Port of the connection part of eufy-security-client p2p/session.js.
/// </summary>
public sealed class P2PSession : IAsyncDisposable
{
    private const int UdpReceiveBufferBytes = 1024 * 1024;
    private const int SioUdpConnReset = -1744830452;

    private readonly StationP2PInfo _station;
    private readonly P2PSessionOptions _options;
    private readonly ILogger _logger;
    private readonly Socket _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<IPEndPoint> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IReadOnlyList<IPEndPoint> _cloudAddresses;

    private IPEndPoint? _candidate;
    private IPEndPoint? _remote;
    private long _connectedAtTicks;
    private long _lastPongTicks;
    private byte[]? _lastPongData;
    private long _dataPackets;
    private Task? _receiveLoop;
    private Task? _heartbeatLoop;

    public P2PSession(StationP2PInfo station, P2PSessionOptions? options = null, ILogger<P2PSession>? logger = null)
    {
        _station = station;
        _options = options ?? new P2PSessionOptions();
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _cloudAddresses = _options.Mode == P2PConnectionMode.Quickest ? CloudIpDecoder.Decode(station.AppConn) : [];

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
        _socket.ReceiveBufferSize = UdpReceiveBufferBytes;
        // Windows reports ICMP "port unreachable" as a ConnectionReset on the next receive; ignore it like Node does.
        if (OperatingSystem.IsWindows())
            _socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
    }

    private volatile P2PSessionState _state = P2PSessionState.Idle;

    public P2PSessionState State
    {
        get => _state;
        private set => _state = value;
    }

    /// <summary>The station's P2P endpoint once connected.</summary>
    public IPEndPoint? RemoteEndPoint => _remote;

    /// <summary>Completes with the reason when the connection ends (station END, heartbeat loss, close).</summary>
    public Task<string> Completion => _completion.Task;

    public TimeSpan? SinceLastPong => _lastPongTicks == 0 ? null : TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastPongTicks));

    public long DataPacketsReceived => Interlocked.Read(ref _dataPackets);

    /// <summary>Raised on the receive loop for every DATA packet (already ACKed). Keep handlers short.</summary>
    public event Action<P2PDataPacket>? DataReceived;

    /// <summary>Raised on the receive loop when the station ACKs our DATA packets.</summary>
    public event Action<P2PDataType, ushort[]>? AckReceived;

    /// <summary>Finds the station and opens the connection. Throws <see cref="TimeoutException"/> if it doesn't answer.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (State != P2PSessionState.Idle)
            throw new InvalidOperationException("A P2P session can only connect once.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var ct = linked.Token;

        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);

        State = P2PSessionState.Lookup;
        var target = await LookupAsync(ct);

        State = P2PSessionState.Connecting;
        _remote = await CheckCamAsync(target, ct);

        State = P2PSessionState.Connected;
        Interlocked.Exchange(ref _connectedAtTicks, DateTime.UtcNow.Ticks);
        _logger.LogInformation("P2P connected to {Station} at {Endpoint}", _station.StationSn, _remote);
        _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(_cts.Token), CancellationToken.None);
    }

    /// <summary>Sends a raw packet to the connected station.</summary>
    public async Task SendAsync(byte[] packet, CancellationToken cancellationToken = default)
    {
        var remote = _remote ?? throw new InvalidOperationException("Not connected.");
        await _socket.SendToAsync(packet, SocketFlags.None, remote, cancellationToken);
    }

    /// <summary>Says goodbye to the station (END) and stops the session.</summary>
    public async Task CloseAsync()
    {
        if (State == P2PSessionState.Connected && _remote is not null)
        {
            try
            {
                await _socket.SendToAsync(P2PPackets.End(), SocketFlags.None, _remote);
                _logger.LogInformation("Closed the P2P connection to {Station} (END)", _station.StationSn);
            }
            catch (SocketException)
            {
                // Closing anyway.
            }
        }
        Finish("closed by client");
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        foreach (var loop in new[] { _receiveLoop, _heartbeatLoop })
            if (loop is not null)
                await loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _socket.Dispose();
        _cts.Dispose();
    }

    private async Task<IPEndPoint> LookupAsync(CancellationToken ct)
    {
        var localTarget = new IPEndPoint(_station.LocalAddress ?? IPAddress.Broadcast, _options.LocalLookupPort);
        var dskKey = _cloudAddresses.Count > 0 && _options.DskKeyProvider is { } provider ? await provider(ct) : null;
        IPEndPoint? ownEndpoint = dskKey is null ? null : GetOwnEndpoint();
        if (_cloudAddresses.Count > 0 && dskKey is null)
            _logger.LogWarning("No DSK key for {Station}; cloud lookup skipped", _station.StationSn);

        _logger.LogDebug("P2P lookup for {Station}: local {Local}, {Cloud} cloud servers", _station.StationSn, localTarget, dskKey is null ? 0 : _cloudAddresses.Count);
        var started = DateTime.UtcNow;
        while (true)
        {
            if (Volatile.Read(ref _candidate) is { } candidate)
                return candidate;
            if (DateTime.UtcNow - started > _options.LookupTimeout)
                throw new TimeoutException($"No P2P lookup answer from {_station.StationSn} within {_options.LookupTimeout.TotalSeconds:0}s");

            await SendQuietAsync(P2PPackets.LocalLookup(), localTarget, ct);
            if (dskKey is not null)
            {
                foreach (var server in _cloudAddresses)
                {
                    await SendQuietAsync(P2PPackets.LookupWithKey(_station.P2PDid, ownEndpoint!, dskKey), server, ct);
                    if (DateTime.UtcNow - started >= _options.Lookup2Delay)
                        await SendQuietAsync(P2PPackets.LookupWithKey2(_station.P2PDid, dskKey), server, ct);
                }
            }
            await WaitForAsync(() => Volatile.Read(ref _candidate) is not null, _options.RetryInterval, ct);
        }
    }

    /// <summary>CHECK_CAM to the port and its ±3 neighbours until the station replies CAM_ID.</summary>
    private async Task<IPEndPoint> CheckCamAsync(IPEndPoint target, CancellationToken ct)
    {
        _logger.LogDebug("P2P CHECK_CAM to {Endpoint}", target);
        var packet = P2PPackets.CheckCam(_station.P2PDid);
        var started = DateTime.UtcNow;
        while (!_connected.Task.IsCompleted)
        {
            if (DateTime.UtcNow - started > _options.ConnectTimeout)
                throw new TimeoutException($"{_station.StationSn} did not answer CHECK_CAM within {_options.ConnectTimeout.TotalSeconds:0}s");

            for (var port = target.Port - 3; port <= target.Port + 3; port++)
                if (port is > 0 and <= ushort.MaxValue)
                    await SendQuietAsync(packet, new IPEndPoint(target.Address, port), ct);
            await WaitForAsync(() => _connected.Task.IsCompleted, _options.RetryInterval, ct);
        }
        return await _connected.Task;
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval);
        var maxSilence = _options.HeartbeatInterval * _options.MaxMissedHeartbeats;
        try
        {
            do
            {
                var lastPong = Interlocked.Read(ref _lastPongTicks);
                var reference = lastPong != 0 ? lastPong : Interlocked.Read(ref _connectedAtTicks);
                if (DateTime.UtcNow - new DateTime(reference, DateTimeKind.Utc) >= maxSilence)
                {
                    _logger.LogWarning("P2P heartbeat lost for {Station}", _station.StationSn);
                    Finish("heartbeat lost");
                    return;
                }
                await SendQuietAsync(P2PPackets.Ping(Volatile.Read(ref _lastPongData)), _remote!, ct);
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // Session finished.
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue;
                }
                var packet = buffer.AsMemory(0, result.ReceivedBytes).ToArray();
                await HandlePacketAsync(packet, (IPEndPoint)result.RemoteEndPoint, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Session finished.
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "P2P socket failed for {Station}", _station.StationSn);
            Finish("socket error: " + ex.Message);
        }
    }

    private async Task HandlePacketAsync(byte[] packet, IPEndPoint from, CancellationToken ct)
    {
        if (!P2PPackets.TryGetType(packet, out var type))
            return;

        switch (type)
        {
            // 0x41 = LOCAL_LOOKUP_RESP from the station (we never receive our own CHECK_CAM).
            case P2PMessageType.LocalLookupResponse when State == P2PSessionState.Lookup:
                var did = P2PPackets.ParseLocalLookupResponse(packet);
                if (did == _station.P2PDid)
                {
                    _logger.LogDebug("LOCAL_LOOKUP_RESP from {Endpoint}", from);
                    Interlocked.CompareExchange(ref _candidate, from, null);
                }
                else
                {
                    _logger.LogDebug("Ignoring LOCAL_LOOKUP_RESP for {Did} from {Endpoint}", did, from);
                }
                break;

            case P2PMessageType.LookupAddr when State == P2PSessionState.Lookup:
                if (P2PPackets.ParseLookupAddr(packet) is { } address &&
                    (_options.Mode == P2PConnectionMode.Quickest || P2PPackets.IsPrivate(address.Address)))
                {
                    _logger.LogDebug("LOOKUP_ADDR {Address} via {Server}", address, from);
                    Interlocked.CompareExchange(ref _candidate, address, null);
                }
                break;

            case P2PMessageType.CamId when Volatile.Read(ref _candidate)?.Address.Equals(from.Address) == true:
                // CAM_ID carries the DID like LOCAL_LOOKUP_RESP; the CHECK_CAM port sweep can reach other stations on that host.
                var camDid = P2PPackets.ParseLocalLookupResponse(packet);
                if (camDid is { Length: > 0 } && camDid != _station.P2PDid)
                {
                    _logger.LogDebug("Ignoring CAM_ID for {Did} from {Endpoint}", camDid, from);
                    break;
                }
                _logger.LogDebug("CAM_ID {Did} from {Endpoint}", camDid, from);
                _connected.TrySetResult(from);
                break;

            case P2PMessageType.Pong:
                Volatile.Write(ref _lastPongData, packet.Length > 4 ? packet[4..] : null);
                Interlocked.Exchange(ref _lastPongTicks, DateTime.UtcNow.Ticks);
                break;

            case P2PMessageType.Ping:
                await SendQuietAsync(P2PPackets.Pong(), from, ct);
                break;

            case P2PMessageType.End:
                _logger.LogInformation("{Station} closed the P2P connection", _station.StationSn);
                Finish("closed by station");
                break;

            case P2PMessageType.Ack:
                if (P2PPackets.ParseAck(packet) is { } ack)
                {
                    _logger.LogTrace("ACK {DataType} {Sequences}", ack.DataType, string.Join(",", ack.Sequences));
                    AckReceived?.Invoke(ack.DataType, ack.Sequences);
                }
                break;

            case P2PMessageType.Data when State == P2PSessionState.Connected:
                if (P2PPackets.ParseData(packet) is not { } data)
                    break;
                await SendQuietAsync(P2PPackets.Ack(data.DataType, data.Sequence), from, ct);
                Interlocked.Increment(ref _dataPackets);
                _logger.LogDebug("DATA {DataType} seq {Sequence} cmd {Command} ({Length} bytes)",
                    data.DataType, data.Sequence, data.CommandId?.ToString() ?? "-", data.Payload.Length);
                DataReceived?.Invoke(data);
                break;

            default:
                _logger.LogTrace("Unhandled P2P packet {Type:X2} ({Length} bytes) from {Endpoint}", (byte)type, packet.Length, from);
                break;
        }
    }

    private void Finish(string reason)
    {
        State = P2PSessionState.Closed;
        _connected.TrySetException(new TimeoutException(reason));
        _connected.Task.Exception?.Handle(_ => true);
        _completion.TrySetResult(reason);
        if (!_cts.IsCancellationRequested)
            _cts.Cancel();
    }

    private async Task SendQuietAsync(byte[] packet, IPEndPoint target, CancellationToken ct)
    {
        try
        {
            await _socket.SendToAsync(packet, SocketFlags.None, target, ct);
        }
        catch (SocketException ex)
        {
            _logger.LogDebug("P2P send to {Target} failed: {Error}", target, ex.SocketErrorCode);
        }
    }

    /// <summary>Our LAN endpoint as seen on the route to the station (needed inside cloud lookup packets).</summary>
    private IPEndPoint GetOwnEndpoint()
    {
        var port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Connect(_station.LocalAddress ?? _cloudAddresses[0].Address, P2PPackets.CloudLookupPort);
        return new IPEndPoint(((IPEndPoint)probe.LocalEndPoint!).Address, port);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < until)
            await Task.Delay(50, ct);
    }
}
