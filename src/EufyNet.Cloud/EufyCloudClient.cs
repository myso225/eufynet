using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EufyNet.Cloud;

/// <summary>
/// Client for the legacy Eufy cloud API: login (2FA / captcha), session restore and the house data
/// needed for P2P. Port of eufy-security-client http/api.js (HTTPApi) — login, request, station/device lists.
/// </summary>
public sealed class EufyCloudClient : IDisposable
{
    private static readonly Uri DomainBase = new("https://extend.eufylife.com/");
    private static readonly TimeSpan RenewBeforeExpiry = TimeSpan.FromDays(1);

    private readonly HttpClient _http;
    private readonly EufyCloudOptions _options;
    private readonly ISessionStore _store;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private readonly Dictionary<string, byte[]> _secrets = [];

    private CloudSessionState _state = new();
    private ECDiffieHellman? _key;
    private bool _initialized;

    public EufyCloudClient(HttpClient http, EufyCloudOptions options, ISessionStore store, ILogger<EufyCloudClient>? logger = null)
    {
        _http = http;
        _options = options;
        _store = store;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public CloudSessionState Session => _state;

    public bool IsLoggedIn => _state.Token is not null && !ExpiresWithin(TimeSpan.Zero);

    /// <summary>
    /// Logs in, or restores the saved session when it is still valid. Call again with an answer when the
    /// result asks for a verification code or captcha.
    /// </summary>
    /// <param name="force">Skip the saved session and do a full login.</param>
    public async Task<LoginResult> LoginAsync(LoginAnswer? answer = null, bool force = false, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _loginLock.WaitAsync(cancellationToken);
        try
        {
            if (answer is null && !force && IsLoggedIn)
            {
                if (await IsProfileValidAsync(cancellationToken))
                {
                    _logger.LogInformation("Restored saved Eufy session for {Email}", _state.Email);
                    return LoginResult.Success;
                }
                _logger.LogInformation("Saved Eufy session is no longer valid, logging in again");
                await InvalidateTokenAsync(cancellationToken);
            }
            return await LoginCoreAsync(answer, cancellationToken);
        }
        finally
        {
            _loginLock.Release();
        }
    }

    public async Task<IReadOnlyList<StationInfo>> GetStationsAsync(CancellationToken cancellationToken = default)
    {
        var data = await PostHouseListAsync("v2/house/station_list", "station list", cancellationToken);
        return data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().Select(StationInfo.FromJson).ToList() : [];
    }

    public async Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var data = await PostHouseListAsync("v2/house/device_list", "device list", cancellationToken);
        return data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().Select(DeviceInfo.FromJson).ToList() : [];
    }

    /// <summary>The station's DSK key, needed for P2P cloud lookups. Null when the cloud has none.</summary>
    public async Task<DskKey?> GetDskKeyAsync(string stationSn, CancellationToken cancellationToken = default)
    {
        var response = await PostAsync("v1/app/equipment/get_dsk_keys", new
        {
            invalid_dsks = new Dictionary<string, string> { [stationSn] = "" },
            station_sns = new[] { stationSn },
            transaction = Transaction(),
        }, cancellationToken);
        if (!response.IsOk)
            throw new EufyApiException($"Eufy DSK keys failed: {response.Code} {response.Message}", response.Code);

        var data = DecryptData(response.Data);
        if (!data.TryGetProperty("dsk_keys", out var keys) || keys.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var key in keys.EnumerateArray())
        {
            if (key.GetStringOrNull("station_sn") == stationSn && key.GetStringOrNull("dsk_key") is { } dsk)
                return new DskKey(dsk, key.GetInt64OrNull("expiration") is { } exp ? DateTimeOffset.FromUnixTimeSeconds(exp) : null);
        }
        return null;
    }

    /// <summary>RSA private key of a P2P cipher (CMD_GATEWAYINFO key unwrap). Null when the cloud returns none.</summary>
    /// <param name="userId">The station's <c>member.admin_user_id</c>.</param>
    public async Task<string?> GetCipherPrivateKeyAsync(int cipherId, string userId, CancellationToken cancellationToken = default)
    {
        var response = await PostAsync("v2/app/cipher/get_ciphers", new
        {
            cipher_ids = new[] { cipherId },
            user_id = userId,
            transaction = Transaction(),
        }, cancellationToken);
        if (!response.IsOk)
            throw new EufyApiException($"Eufy get ciphers failed: {response.Code} {response.Message}", response.Code);

        var data = DecryptData(response.Data);
        if (data.ValueKind != JsonValueKind.Array)
            return null;
        return data.EnumerateArray()
            .Where(c => c.GetInt32OrNull("cipher_id") == cipherId)
            .Select(c => c.GetStringOrNull("private_key"))
            .FirstOrDefault(k => !string.IsNullOrEmpty(k));
    }

    /// <summary>Authorized POST to any API endpoint; re-logs in once on 401.</summary>
    public Task<ApiResponse> PostAsync(string endpoint, object body, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync(HttpMethod.Post, endpoint, body, cancellationToken);

    public Task<ApiResponse> GetAsync(string endpoint, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync(HttpMethod.Get, endpoint, null, cancellationToken);

    /// <summary>Decrypts an encrypted <c>data</c> string with the session's server key; other values pass through.</summary>
    public JsonElement DecryptData(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.String)
            return data;
        try
        {
            var json = EufyCrypto.DecryptApiString(data.GetString()!, SharedSecret(_state.ServerPublicKey!));
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            throw new EufyApiException("Could not decrypt API data; log in again with --force-login", inner: ex);
        }
    }

    public void Dispose()
    {
        _key?.Dispose();
        _loginLock.Dispose();
    }

    private async Task<LoginResult> LoginCoreAsync(LoginAnswer? answer, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["ab"] = _state.Country,
            ["client_secret_info"] = new { public_key = _state.ClientPublicKey },
            ["enc"] = 0,
            ["email"] = _options.Username,
            ["password"] = EufyCrypto.EncryptApiData(_options.Password, SharedSecret(EufyCrypto.StaticServerPublicKeyHex)),
            ["time_zone"] = TimeZoneOffsetMs(),
            ["transaction"] = Transaction(),
        };
        if (answer?.VerifyCode is { } verifyCode)
        {
            body["verify_code"] = verifyCode;
        }
        else if (answer?.CaptchaId is { } captchaId)
        {
            body["captcha_id"] = captchaId;
            body["answer"] = answer.CaptchaAnswer;
        }

        var (status, response) = await SendRawAsync(HttpMethod.Post, "v2/passport/login_sec", body, cancellationToken);
        if (response is null)
            return new LoginResult(LoginStatus.Failed, Message: $"HTTP {(int)status}");

        _logger.LogDebug("login_sec returned code {Code} ({Message})", response.Code, response.Message);
        switch (response.Code)
        {
            case EufyResponseCode.Ok:
                await CompleteLoginAsync(response.Data, cancellationToken);
                if (answer?.VerifyCode is { } code)
                    await EnsureTrustedDeviceAsync(code, cancellationToken);
                _logger.LogInformation("Logged in to Eufy cloud as {Email}", _state.Email);
                return LoginResult.Success;

            case EufyResponseCode.NeedVerifyCode:
                // The temporary token from this response authorizes the verify-code request and the retry.
                SetToken(response.Data);
                await SendVerifyCodeAsync(cancellationToken);
                return new LoginResult(LoginStatus.NeedVerifyCode, response.Code, response.Message);

            case EufyResponseCode.LoginNeedCaptcha:
            case EufyResponseCode.LoginCaptchaError:
                var captcha = new CaptchaChallenge(
                    response.Data.GetStringOrNull("captcha_id") ?? "",
                    response.Data.GetStringOrNull("item") ?? "");
                return new LoginResult(LoginStatus.NeedCaptcha, response.Code, response.Message, captcha);

            case EufyResponseCode.VerifyCodeError:
            case EufyResponseCode.VerifyCodeExpired:
            case EufyResponseCode.VerifyCodeNoneMatch:
                return new LoginResult(LoginStatus.VerifyCodeRejected, response.Code, response.Message);

            case EufyResponseCode.PasswordTooManyIncorrect:
            case EufyResponseCode.PasswordWrongFiveTimes:
            case EufyResponseCode.MaxLoginLimit:
            case EufyResponseCode.VerifyCodeMax:
                return new LoginResult(LoginStatus.Locked, response.Code, response.Message);

            default:
                return new LoginResult(LoginStatus.Failed, response.Code, response.Message);
        }
    }

    private async Task CompleteLoginAsync(JsonElement data, CancellationToken cancellationToken)
    {
        if (data.TryGetProperty("server_secret_info", out var secretInfo) && secretInfo.GetStringOrNull("public_key") is { } serverKey)
            _state.ServerPublicKey = serverKey;

        SetToken(data);
        _state.UserId = data.GetStringOrNull("user_id");
        _state.NickName = data.GetStringOrNull("nick_name");
        _state.Email = TryDecryptString(data.GetStringOrNull("email")) ?? _options.Username;
        await _store.SaveAsync(_state, cancellationToken);
    }

    private void SetToken(JsonElement data)
    {
        _state.Token = data.GetStringOrNull("auth_token");
        _state.TokenExpiresAt = data.GetInt64OrNull("token_expires_at") is { } seconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    private async Task InvalidateTokenAsync(CancellationToken cancellationToken)
    {
        _state.Token = null;
        _state.TokenExpiresAt = null;
        _state.ServerPublicKey = EufyCrypto.StaticServerPublicKeyHex;
        await _store.SaveAsync(_state, cancellationToken);
    }

    private async Task SendVerifyCodeAsync(CancellationToken cancellationToken)
    {
        const int typeEmail = 2;
        var (_, response) = await SendRawAsync(HttpMethod.Post, "v1/sms/send/verify_code",
            new { message_type = typeEmail, transaction = Transaction() }, cancellationToken);
        if (response?.IsOk == true)
            _logger.LogInformation("Eufy sent a verification code by email");
        else
            _logger.LogWarning("Requesting the verification code failed: {Code} {Message}", response?.Code, response?.Message);
    }

    /// <summary>After a 2FA login, register this "phone" as trusted so later logins skip 2FA.</summary>
    private async Task EnsureTrustedDeviceAsync(string verifyCode, CancellationToken cancellationToken)
    {
        var (_, list) = await SendRawAsync(HttpMethod.Get, "v1/app/trust_device/list", null, cancellationToken);
        if (list?.IsOk == true && list.Data.TryGetProperty("list", out var devices) && devices.ValueKind == JsonValueKind.Array &&
            devices.EnumerateArray().Any(d => d.GetInt32OrNull("is_current_device") == 1))
            return;

        var (_, added) = await SendRawAsync(HttpMethod.Post, "v1/app/trust_device/add",
            new { verify_code = verifyCode, transaction = Transaction() }, cancellationToken);
        if (added?.IsOk == true)
            _logger.LogInformation("Registered {PhoneModel} as a trusted device", _state.PhoneModel);
        else
            _logger.LogWarning("Adding trusted device failed: {Code} {Message}", added?.Code, added?.Message);
    }

    private async Task<bool> IsProfileValidAsync(CancellationToken cancellationToken)
    {
        var (_, response) = await SendRawAsync(HttpMethod.Get, "v2/passport/profile", null, cancellationToken);
        return response?.IsOk == true;
    }

    private async Task<JsonElement> PostHouseListAsync(string endpoint, string what, CancellationToken cancellationToken)
    {
        var response = await PostAsync(endpoint, new
        {
            device_sn = "",
            num = 1000,
            orderby = "",
            page = 0,
            station_sn = "",
            time_zone = TimeZoneOffsetMs(),
            transaction = Transaction(),
        }, cancellationToken);
        if (!response.IsOk)
            throw new EufyApiException($"Eufy {what} failed: {response.Code} {response.Message}", response.Code);
        return DecryptData(response.Data);
    }

    private async Task<ApiResponse> SendAuthorizedAsync(HttpMethod method, string endpoint, object? body, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        if (_state.Token is null || ExpiresWithin(RenewBeforeExpiry))
            await ReloginAsync(cancellationToken);

        var (status, response) = await SendRawAsync(method, endpoint, body, cancellationToken);
        if (status == HttpStatusCode.Unauthorized || response?.Code == EufyResponseCode.SessionTimeout)
        {
            _logger.LogInformation("Eufy token rejected, logging in again");
            await InvalidateTokenAsync(cancellationToken);
            await ReloginAsync(cancellationToken);
            (status, response) = await SendRawAsync(method, endpoint, body, cancellationToken);
        }
        return response ?? throw new EufyApiException($"{endpoint} returned HTTP {(int)status}");
    }

    private async Task ReloginAsync(CancellationToken cancellationToken)
    {
        var result = await LoginAsync(force: true, cancellationToken: cancellationToken);
        if (result.Status != LoginStatus.Ok)
            throw new EufyLoginRequiredException(result);
    }

    /// <summary>Sends one request without any re-login handling. Returns no response for HTTP errors.</summary>
    private async Task<(HttpStatusCode Status, ApiResponse? Response)> SendRawAsync(
        HttpMethod method, string endpoint, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(_state.ApiBase! + "/"), endpoint));
        foreach (var (name, value) in BuildHeaders())
            request.Headers.TryAddWithoutValidation(name, value);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var httpResponse = await _http.SendAsync(request, cancellationToken);
        if (!httpResponse.IsSuccessStatusCode)
        {
            _logger.LogDebug("{Method} {Endpoint} -> HTTP {Status}", method, endpoint, (int)httpResponse.StatusCode);
            return (httpResponse.StatusCode, null);
        }

        var text = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var response = new ApiResponse(
            root.GetInt32OrNull("code") ?? -1,
            root.GetStringOrNull("msg"),
            root.TryGetProperty("data", out var data) ? data.Clone() : default);
        _logger.LogDebug("{Method} {Endpoint} -> code {Code}", method, endpoint, response.Code);
        return (httpResponse.StatusCode, response);
    }

    /// <summary>Fake Android-app headers; got lower-cases header names, so these are sent as-is.</summary>
    private IEnumerable<(string, string)> BuildHeaders()
    {
        yield return ("app_version", "v4.6.0_1630");
        yield return ("os_type", "android");
        yield return ("os_version", "31");
        yield return ("phone_model", _state.PhoneModel!.ToUpperInvariant());
        yield return ("country", _state.Country!);
        yield return ("language", _options.Language);
        yield return ("openudid", _state.OpenUdid!);
        yield return ("net_type", "wifi");
        yield return ("mnc", "02");
        yield return ("mcc", "262");
        yield return ("sn", _state.SerialNumber!);
        yield return ("model_type", "PHONE");
        yield return ("timezone", TimeZoneGmtString());
        yield return ("cache-control", "no-cache");
        if (_state.UserId is { } userId)
            yield return ("gtoken", EufyCrypto.Md5Hex(userId));
        if (_state.Token is { } token)
            yield return ("x-auth-token", token);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        _state = await _store.LoadAsync(cancellationToken) ?? new CloudSessionState();
        var country = _options.Country.ToUpperInvariant();
        var loginHash = EufyCrypto.Md5Hex($"{_options.Username}:{_options.Password}");
        if (_state.LoginHash != loginHash || _state.Country != country)
        {
            if (_state.LoginHash is not null)
                _logger.LogInformation("Credentials or country changed, dropping the saved Eufy session");
            _state.ResetLogin();
            _state.LoginHash = loginHash;
            _state.Country = country;
        }

        _key = LoadOrCreateKey();
        if (_state.ServerPublicKey is null || !IsUsableServerKey(_state.ServerPublicKey))
            _state.ServerPublicKey = EufyCrypto.StaticServerPublicKeyHex;

        _state.OpenUdid ??= EufyCrypto.RandomHex(8);
        _state.SerialNumber ??= EufyCrypto.RandomHex(6);
        if (!string.IsNullOrWhiteSpace(_options.TrustedDeviceName))
            _state.PhoneModel = _options.TrustedDeviceName;
        _state.PhoneModel ??= "eufynet";

        _state.ApiBase ??= await ResolveApiBaseAsync(country, cancellationToken);
        await _store.SaveAsync(_state, cancellationToken);
        _initialized = true;
    }

    private ECDiffieHellman LoadOrCreateKey()
    {
        if (_state.ClientPrivateKey is not null && _state.ClientPublicKey is not null)
        {
            try
            {
                return EufyCrypto.ImportKey(_state.ClientPrivateKey, _state.ClientPublicKey);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            {
                _logger.LogWarning("Saved client key is invalid, generating a new one");
            }
        }

        var key = EufyCrypto.CreateKey();
        _state.ClientPrivateKey = EufyCrypto.ExportPrivateKeyHex(key);
        _state.ClientPublicKey = EufyCrypto.ExportPublicKeyHex(key);
        _state.ResetLogin();
        return key;
    }

    private bool IsUsableServerKey(string serverPublicKey)
    {
        try
        {
            SharedSecret(serverPublicKey);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private async Task<string> ResolveApiBaseAsync(string country, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(new Uri(DomainBase, $"domain/{country}"), cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        if (root.GetInt32OrNull("code") == EufyResponseCode.Ok &&
            root.TryGetProperty("data", out var data) && data.GetStringOrNull("domain") is { } domain)
        {
            _logger.LogInformation("Eufy API server for {Country}: {Domain}", country, domain);
            return $"https://{domain}";
        }
        throw new EufyApiException($"Could not resolve the Eufy API server for country {country}: {root.GetStringOrNull("msg")}",
            root.GetInt32OrNull("code"));
    }

    private byte[] SharedSecret(string serverPublicKey)
    {
        if (!_secrets.TryGetValue(serverPublicKey, out var secret))
            _secrets[serverPublicKey] = secret = EufyCrypto.DeriveSharedSecret(_key!, serverPublicKey);
        return secret;
    }

    private string? TryDecryptString(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
            return null;
        try
        {
            return EufyCrypto.DecryptApiString(base64, SharedSecret(_state.ServerPublicKey!));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    private bool ExpiresWithin(TimeSpan margin) =>
        _state.TokenExpiresAt is { } expiresAt && DateTimeOffset.UtcNow + margin >= expiresAt;

    private static string Transaction() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

    private static long TimeZoneOffsetMs() => (long)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMilliseconds;

    internal static string TimeZoneGmtString(TimeSpan? offset = null)
    {
        var o = offset ?? TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
        var sign = o < TimeSpan.Zero ? "-" : "+";
        var abs = o.Duration();
        return $"GMT{sign}{abs.Hours:00}:{abs.Minutes:00}";
    }
}
