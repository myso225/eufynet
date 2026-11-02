using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using EufyNet.Cloud;
using EufyNet.Core;
using EufyNet.P2P;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SysConsole = System.Console;

namespace EufyNet.Transport.Console;

public sealed class ConsoleTransportOptions
{
    /// <summary>Where a login captcha image is written for the user to open.</summary>
    public string CaptchaFile { get; set; } = Path.Combine("data", "captcha.png");
}

/// <summary>
/// Events and state as log lines, commands from stdin. Without console input (Docker without <c>-it</c>) it only logs.
/// </summary>
public sealed class ConsoleTransport(IHostApplicationLifetime lifetime, ConsoleTransportOptions options, ILogger<ConsoleTransport> logger)
    : IBridgeTransport
{
    private readonly ConcurrentDictionary<string, StationState> _states = new();
    private LoginState? _login;
    private int _nextId;

    public async Task PublishLoginAsync(LoginState state, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _login, state);
        switch (state.Status)
        {
            case LoginStatus.NeedVerifyCode:
                SysConsole.WriteLine("Enter the verification code Eufy emailed you:");
                break;
            case LoginStatus.VerifyCodeRejected:
                SysConsole.WriteLine("Code rejected or expired. Enter it again (empty line = send a new code):");
                break;
            case LoginStatus.NeedCaptcha when state.Captcha is { } captcha:
                var path = Path.GetFullPath(options.CaptchaFile);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, DecodeImage(captcha.Image), cancellationToken);
                SysConsole.WriteLine($"Eufy wants a captcha. Open {path} and type the characters:");
                break;
            case LoginStatus.Ok:
                logger.LogInformation("Logged in to the Eufy cloud");
                break;
            case null:
                break;
            default:
                logger.LogError("Eufy login {Status}: {Message}", state.Status, state.Message);
                break;
        }
    }

    public Task PublishStateAsync(StationState state, CancellationToken cancellationToken)
    {
        var previous = _states.GetValueOrDefault(state.StationSn);
        _states[state.StationSn] = state;
        if (previous?.Connected != state.Connected && (previous is not null || state.Connected))
        {
            if (state.Connected)
                logger.LogInformation("{Station} ready: guard mode {GuardMode}, encryption {Encryption}. Type help for commands",
                    state.StationSn, state.GuardMode?.ToString() ?? "unknown", state.Encryption);
            else
                logger.LogWarning("{Station} disconnected", state.StationSn);
        }
        return Task.CompletedTask;
    }

    public Task PublishEventAsync(BridgeEvent bridgeEvent, CancellationToken cancellationToken)
    {
        var e = bridgeEvent.Event;
        logger.LogInformation("Event {EventType} {Device}: {Description}", e.GetType().Name.Replace("Event", ""),
            bridgeEvent.DeviceName ?? e.StationSn, EventText.Describe(e));
        return Task.CompletedTask;
    }

    public Task PublishResultAsync(BridgeCommandResult result, CancellationToken cancellationToken)
    {
        if (!result.Success)
        {
            logger.LogWarning("{Command} failed: {Message}", result.Name, result.Message);
            return Task.CompletedTask;
        }

        switch (result.Name)
        {
            case BridgeCommands.Status when result.Data is { } data:
                var states = data.Deserialize<List<StationState>>() ?? [];
                if (states.Count == 0)
                    logger.LogInformation("No station");
                foreach (var s in states)
                    logger.LogInformation("{Station}: {Connection}, guard mode {GuardMode}, alarm mode {AlarmMode}, encryption {Encryption}. {Cameras}",
                        s.StationSn, s.Connected ? "connected" : "disconnected", s.GuardMode?.ToString() ?? "unknown",
                        s.AlarmMode?.ToString() ?? "unknown", s.Encryption?.ToString() ?? "-",
                        string.Join("; ", s.Cameras.Select(c => $"{c.Name}: last motion {Time(c.LastMotion)}, last recording {Time(c.LastRecording)}, "
                            + $"{c.Motions} motions / {c.Recordings} recordings in {s.ActivityWindow.TotalHours:0.#} h, battery {(c.Battery is { } b ? $"{b}%" : "?")}")));
                break;
            case BridgeCommands.Info or BridgeCommands.Clips when result.Data is { } data:
                logger.LogInformation("{Command}: {Json}", result.Name, EventText.Truncate(data.GetRawText(), 2000));
                break;
            default:
                if (result.Message is not null)
                    logger.LogInformation("{Command}: {Message}", result.Name, result.Message);
                break;
        }
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<BridgeCommand> ReadCommandsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // ReadLine cannot be cancelled; WaitAsync lets shutdown go on while it blocks.
            var line = await Task.Run(SysConsole.ReadLine, CancellationToken.None).WaitAsync(cancellationToken);
            if (line is null)
                yield break; // no console input
            var input = ConsoleCommandParser.Parse(line, Volatile.Read(ref _login), $"console-{Interlocked.Increment(ref _nextId)}");
            if (input.Quit)
            {
                lifetime.StopApplication();
                yield break;
            }
            if (input.Help is not null)
                SysConsole.WriteLine(input.Help);
            if (input.Command is { } command)
                yield return command;
        }
    }

    private static string Time(DateTimeOffset? time) => time is { } t ? t.ToLocalTime().ToString("HH:mm:ss") : "-";

    private static byte[] DecodeImage(string image)
    {
        var comma = image.IndexOf(',');
        return Convert.FromBase64String(image.StartsWith("data:", StringComparison.Ordinal) && comma >= 0 ? image[(comma + 1)..] : image);
    }
}
