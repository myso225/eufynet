using System.Text.Json;

namespace EufyNet.Cloud;

public sealed class EufyCloudOptions
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>ISO country code of the account (picks the regional API server).</summary>
    public string Country { get; set; } = "US";
    public string Language { get; set; } = "en";
    /// <summary>Phone model reported to Eufy; shows up in the app's trusted-device list.</summary>
    public string TrustedDeviceName { get; set; } = "eufynet";
}

/// <summary>Response codes of the legacy API (subset of eufy-security-client http/types.js ResponseErrorCode).</summary>
public static class EufyResponseCode
{
    public const int Ok = 0;
    public const int SessionTimeout = 401;
    public const int PasswordTooManyIncorrect = 10019;
    public const int VerifyCodeError = 26050;
    public const int VerifyCodeExpired = 26051;
    public const int NeedVerifyCode = 26052;
    public const int VerifyCodeMax = 26053;
    public const int VerifyCodeNoneMatch = 26054;
    public const int EmailOrPasswordIncorrect = 26108;
    public const int MaxLoginLimit = 100028;
    public const int LoginNeedCaptcha = 100032;
    public const int LoginCaptchaError = 100033;
    public const int PasswordWrongFiveTimes = 100056;
}

/// <summary>Standard response envelope: <c>{ code, msg, data }</c>.</summary>
public sealed record ApiResponse(int Code, string? Message, JsonElement Data)
{
    public bool IsOk => Code == EufyResponseCode.Ok;
}

public enum LoginStatus
{
    Ok,
    /// <summary>Eufy emailed a code; call LoginAsync again with <see cref="LoginAnswer.WithVerifyCode"/>.</summary>
    NeedVerifyCode,
    /// <summary>The submitted code was wrong or expired. Retry, or log in without an answer to get a new one.</summary>
    VerifyCodeRejected,
    /// <summary>Solve <see cref="LoginResult.Captcha"/> and call LoginAsync with <see cref="LoginAnswer.WithCaptcha"/>.</summary>
    NeedCaptcha,
    /// <summary>The backend locked the account temporarily; retrying makes it worse.</summary>
    Locked,
    Failed,
}

/// <param name="Image">Captcha image as sent by Eufy (usually a data:image/png;base64 URL).</param>
public sealed record CaptchaChallenge(string CaptchaId, string Image);

public sealed record LoginResult(LoginStatus Status, int? Code = null, string? Message = null, CaptchaChallenge? Captcha = null)
{
    public static readonly LoginResult Success = new(LoginStatus.Ok);
}

public sealed record LoginAnswer(string? VerifyCode, string? CaptchaId, string? CaptchaAnswer)
{
    public static LoginAnswer WithVerifyCode(string code) => new(code, null, null);
    public static LoginAnswer WithCaptcha(string captchaId, string answer) => new(null, captchaId, answer);
}

public sealed record StationInfo(
    string StationSn,
    string? Name,
    string? Model,
    int? DeviceType,
    string? IpAddress,
    string? P2PDid,
    string? AppConn,
    string? SoftwareVersion,
    string? AdminUserId,
    string? MemberNickName,
    JsonElement Raw)
{
    public static StationInfo FromJson(JsonElement e)
    {
        var member = e.TryGetProperty("member", out var m) ? m : default;
        return new StationInfo(
            e.GetStringOrNull("station_sn") ?? "",
            e.GetStringOrNull("station_name"),
            e.GetStringOrNull("station_model"),
            e.GetInt32OrNull("device_type"),
            e.GetStringOrNull("ip_addr"),
            e.GetStringOrNull("p2p_did"),
            e.GetStringOrNull("app_conn"),
            e.GetStringOrNull("main_sw_version"),
            member.GetStringOrNull("admin_user_id"),
            member.GetStringOrNull("nick_name"),
            e.Clone());
    }

    /// <summary>Value of a cloud parameter (<c>params[].param_type</c>), e.g. 1224 = guard mode.</summary>
    public string? GetParam(int paramType)
    {
        if (!Raw.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Array)
            return null;
        return parameters.EnumerateArray()
            .Where(p => p.GetInt32OrNull("param_type") == paramType)
            .Select(p => p.GetStringOrNull("param_value"))
            .FirstOrDefault();
    }
}

public sealed record DeviceInfo(
    string DeviceSn,
    string? Name,
    string? Model,
    int? DeviceType,
    string? StationSn,
    int? Channel,
    JsonElement Raw)
{
    public static DeviceInfo FromJson(JsonElement e) => new(
        e.GetStringOrNull("device_sn") ?? "",
        e.GetStringOrNull("device_name"),
        e.GetStringOrNull("device_model"),
        e.GetInt32OrNull("device_type"),
        e.GetStringOrNull("station_sn"),
        e.GetInt32OrNull("device_channel"),
        e.Clone());

    /// <summary>A cloud parameter's value and when the cloud last changed it, e.g. 1101 = battery %.</summary>
    public (string? Value, DateTimeOffset? Updated) GetParam(int paramType)
    {
        if (!Raw.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Array)
            return (null, null);
        foreach (var p in parameters.EnumerateArray().Where(p => p.GetInt32OrNull("param_type") == paramType))
        {
            var updated = p.TryGetProperty("update_time", out var u) && u.TryGetInt64(out var seconds) && seconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime()
                : (DateTimeOffset?)null;
            return (p.GetStringOrNull("param_value"), updated);
        }
        return (null, null);
    }
}

public sealed record DskKey(string Key, DateTimeOffset? Expiration);

public class EufyApiException(string message, int? code = null, Exception? inner = null) : Exception(message, inner)
{
    public int? Code { get; } = code;
}

/// <summary>A request needed a fresh login that requires user input (2FA, captcha) or failed.</summary>
public sealed class EufyLoginRequiredException(LoginResult result)
    : EufyApiException($"Eufy login required: {result.Status} {result.Message}", result.Code)
{
    public LoginResult Result { get; } = result;
}

internal static class JsonElementExtensions
{
    /// <summary>Eufy mixes strings and numbers for the same fields; read either as a string.</summary>
    public static string? GetStringOrNull(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    public static long? GetInt64OrNull(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
            return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out n))
            return n;
        return null;
    }

    public static int? GetInt32OrNull(this JsonElement e, string name) =>
        e.GetInt64OrNull(name) is { } n && n is >= int.MinValue and <= int.MaxValue ? (int)n : null;
}
