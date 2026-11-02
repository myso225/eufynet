using System.Text.Json;
using EufyNet.Cloud;
using EufyNet.P2P;

namespace EufyNet.Core;

/// <summary>
/// The outside world of the bridge: console now, MQTT later. It shows login, state and events, and supplies commands.
/// The driver knows nothing about it; <see cref="TransportBridge"/> connects the two.
/// </summary>
public interface IBridgeTransport
{
    Task PublishLoginAsync(LoginState state, CancellationToken cancellationToken);

    Task PublishStateAsync(StationState state, CancellationToken cancellationToken);

    Task PublishEventAsync(BridgeEvent bridgeEvent, CancellationToken cancellationToken);

    Task PublishResultAsync(BridgeCommandResult result, CancellationToken cancellationToken);

    /// <summary>Commands until the transport has no more (e.g. stdin closed) or the token is cancelled.</summary>
    IAsyncEnumerable<BridgeCommand> ReadCommandsAsync(CancellationToken cancellationToken);
}

/// <summary>What <see cref="TransportBridge"/> needs from the driver; <see cref="EufyDriver"/> implements it.</summary>
public interface IEufyDriver
{
    event Action<LoginState>? LoginChanged;

    event Action<StationState>? StateChanged;

    event Action<BridgeEvent>? EventRaised;

    Task<BridgeCommandResult> ExecuteAsync(BridgeCommand command, CancellationToken cancellationToken);
}

/// <summary>Cloud login progress. <see cref="Status"/> is null while logging in.</summary>
public sealed record LoginState(LoginStatus? Status, string? Message = null, CaptchaChallenge? Captcha = null)
{
    /// <summary>The login waits for a verification code (<see cref="BridgeCommands.VerifyCode"/> / <see cref="BridgeCommands.ResendCode"/>).</summary>
    public bool NeedsVerifyCode => Status is LoginStatus.NeedVerifyCode or LoginStatus.VerifyCodeRejected;

    /// <summary>The login waits for <see cref="BridgeCommands.Captcha"/>.</summary>
    public bool NeedsCaptcha => Status == LoginStatus.NeedCaptcha;
}

/// <param name="Motions">Motions within <see cref="StationState.ActivityWindow"/>.</param>
/// <param name="Recordings">Recordings saved within <see cref="StationState.ActivityWindow"/>.</param>
/// <param name="Battery">Percent; from the station when it reports it, otherwise the cloud.</param>
/// <param name="ImageTakenAt">When the last downloaded picture (<see cref="CameraImageEvent"/>) was taken.</param>
public sealed record CameraState(string DeviceSn, string Name, int? Channel, DateTimeOffset? LastMotion, DateTimeOffset? LastRecording,
    int Motions = 0, int Recordings = 0, int? Battery = null, DateTimeOffset? BatteryUpdated = null, DateTimeOffset? ImageTakenAt = null);

public sealed record StationState(string StationSn, string Name, bool Connected, GuardMode? GuardMode, GuardMode? AlarmMode,
    EncryptionLevel? Encryption, IReadOnlyList<CameraState> Cameras, TimeSpan ActivityWindow = default);

/// <summary>A station event with the camera it concerns, when known.</summary>
public sealed record BridgeEvent(StationEvent Event, string? DeviceSn, string? DeviceName);

/// <summary>A request from the transport. <see cref="StationSn"/> null = the first connected station.</summary>
public sealed record BridgeCommand(string Id, string Name, string? StationSn = null, IReadOnlyDictionary<string, string>? Args = null)
{
    public string? Arg(string name) => Args is not null && Args.TryGetValue(name, out var value) ? value : null;
}

public sealed record BridgeCommandResult(string Id, string Name, bool Success, string? Message = null, JsonElement? Data = null);

/// <summary>Command names and their arguments.</summary>
public static class BridgeCommands
{
    /// <summary>Data: all <see cref="StationState"/>s.</summary>
    public const string Status = "status";

    /// <summary>Arg <c>mode</c>: a <see cref="GuardMode"/> name. Changes the real alarm.</summary>
    public const string SetGuardMode = "set_guard_mode";

    /// <summary>Data: the station's CMD_CAMERA_INFO JSON. Read-only.</summary>
    public const string Info = "info";

    /// <summary>Optional arg <c>camera</c> (name or serial). Data: latest record info and the local record list. Read-only.</summary>
    public const string Clips = "clips";

    /// <summary>Optional arg <c>camera</c> (name or serial). Downloads each camera's newest crop picture (<see cref="CameraImageEvent"/>). Read-only.</summary>
    public const string Picture = "picture";

    /// <summary>Arg <c>code</c>: the code Eufy emailed.</summary>
    public const string VerifyCode = "verify_code";

    /// <summary>Ask Eufy for a new verification code.</summary>
    public const string ResendCode = "resend_code";

    /// <summary>Args <c>id</c> and <c>answer</c>.</summary>
    public const string Captcha = "captcha";
}
