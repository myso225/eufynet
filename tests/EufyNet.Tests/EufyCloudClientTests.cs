using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EufyNet.Cloud;

namespace EufyNet.Tests;

public class EufyCloudClientTests
{
    private static readonly EufyCloudOptions Options = new()
    {
        Username = "me@example.com",
        Password = "secret",
        Country = "sk",
        TrustedDeviceName = "eufynet-test",
    };

    [Fact]
    public async Task Two_factor_login_trusts_device_and_decrypts_station_list()
    {
        var server = new FakeEufyServer();
        var store = new InMemorySessionStore();
        using var client = new EufyCloudClient(new HttpClient(server), Options, store);

        var first = await client.LoginAsync();
        Assert.Equal(LoginStatus.NeedVerifyCode, first.Status);
        var sendCode = server.Single("/v1/sms/send/verify_code");
        Assert.Equal("tmp-token", sendCode.Headers["x-auth-token"]);
        Assert.Equal(2, sendCode.Body!["message_type"]!.GetValue<int>());

        var second = await client.LoginAsync(LoginAnswer.WithVerifyCode("123456"));
        Assert.Equal(LoginStatus.Ok, second.Status);
        Assert.Equal("123456", server.Requests.Last(r => r.Path == "/v2/passport/login_sec").Body!["verify_code"]!.GetValue<string>());
        Assert.Equal("123456", server.Single("/v1/app/trust_device/add").Body!["verify_code"]!.GetValue<string>());
        Assert.Equal("me@example.com", client.Session.Email);
        Assert.Equal(FakeEufyServer.Server2Public, client.Session.ServerPublicKey);

        var stations = await client.GetStationsAsync();
        var station = Assert.Single(stations);
        Assert.Equal("T8010P0000000001", station.StationSn);
        Assert.Equal("EUFYCAM-123456-ABCDE", station.P2PDid);
        Assert.Equal("42", station.AdminUserId);

        var stationRequest = server.Single("/v2/house/station_list");
        Assert.Equal("final-token", stationRequest.Headers["x-auth-token"]);
        Assert.Equal(EufyCrypto.Md5Hex("uid-1"), stationRequest.Headers["gtoken"]);
        Assert.Equal("SK", stationRequest.Headers["country"]);
        Assert.Equal("EUFYNET-TEST", stationRequest.Headers["phone_model"]);
    }

    [Fact]
    public async Task Saved_session_is_restored_without_login()
    {
        var server = new FakeEufyServer();
        var store = new InMemorySessionStore();
        using (var client = new EufyCloudClient(new HttpClient(server), Options, store))
        {
            await client.LoginAsync();
            await client.LoginAsync(LoginAnswer.WithVerifyCode("123456"));
        }
        var loginsBefore = server.Count("/v2/passport/login_sec");

        using var restored = new EufyCloudClient(new HttpClient(server), Options, store);
        var result = await restored.LoginAsync();

        Assert.Equal(LoginStatus.Ok, result.Status);
        Assert.Equal(loginsBefore, server.Count("/v2/passport/login_sec"));
        Assert.Equal(1, server.Count("/v2/passport/profile"));
        Assert.Single(await restored.GetStationsAsync());
    }

    [Fact]
    public async Task Changed_password_drops_saved_session()
    {
        var server = new FakeEufyServer();
        var store = new InMemorySessionStore();
        using (var client = new EufyCloudClient(new HttpClient(server), Options, store))
        {
            await client.LoginAsync();
            await client.LoginAsync(LoginAnswer.WithVerifyCode("123456"));
        }

        var loginsBefore = server.Count("/v2/passport/login_sec");

        var changed = new EufyCloudOptions { Username = Options.Username, Password = "other", Country = Options.Country };
        using var relogin = new EufyCloudClient(new HttpClient(server), changed, store);
        await relogin.LoginAsync();

        Assert.Equal(loginsBefore + 1, server.Count("/v2/passport/login_sec"));
        Assert.Equal(0, server.Count("/v2/passport/profile"));
        Assert.Equal(2, server.Count("/domain/SK"));
    }

    [Fact]
    public async Task Unauthorized_request_logs_in_again_and_retries()
    {
        var server = new FakeEufyServer();
        var store = new InMemorySessionStore();
        using var client = new EufyCloudClient(new HttpClient(server), Options, store);
        await client.LoginAsync();
        await client.LoginAsync(LoginAnswer.WithVerifyCode("123456"));

        server.TrustedDevice = true; // trusted now, so the re-login needs no code
        server.FailNextStationListWith401 = true;
        var stations = await client.GetStationsAsync();

        Assert.Single(stations);
        Assert.Equal(2, server.Count("/v2/house/station_list"));
    }

    [Fact]
    public async Task Captcha_challenge_is_surfaced()
    {
        var server = new FakeEufyServer { RequireCaptcha = true };
        using var client = new EufyCloudClient(new HttpClient(server), Options, new InMemorySessionStore());

        var result = await client.LoginAsync();

        Assert.Equal(LoginStatus.NeedCaptcha, result.Status);
        Assert.Equal("cap-1", result.Captcha!.CaptchaId);
        Assert.StartsWith("data:image/png;base64,", result.Captcha.Image);
    }
}

internal sealed class InMemorySessionStore : ISessionStore
{
    private string? _json;

    public Task<CloudSessionState?> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_json is null ? null : JsonSerializer.Deserialize<CloudSessionState>(_json));

    public Task SaveAsync(CloudSessionState state, CancellationToken cancellationToken = default)
    {
        _json = JsonSerializer.Serialize(state);
        return Task.CompletedTask;
    }
}

internal sealed record RecordedRequest(string Method, string Path, Dictionary<string, string> Headers, JsonNode? Body);

/// <summary>Minimal stand-in for the Eufy cloud: domain lookup, login_sec with 2FA, trust device, profile, station list.</summary>
internal sealed class FakeEufyServer : HttpMessageHandler
{
    public const string Server2Public = EufyCryptoTests.Server2Public;

    public List<RecordedRequest> Requests { get; } = [];
    public bool TrustedDevice { get; set; }
    public bool RequireCaptcha { get; set; }
    public bool FailNextStationListWith401 { get; set; }

    private string? _validToken;
    private string? _clientPublicKey;

    public RecordedRequest Single(string path) => Assert.Single(Requests, r => r.Path == path);
    public int Count(string path) => Requests.Count(r => r.Path == path);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add(new RecordedRequest(request.Method.Method, path, headers, body));
        headers.TryGetValue("x-auth-token", out var token);

        return path switch
        {
            "/domain/SK" => Json(new { code = 0, data = new { domain = "api.test" } }),
            "/v2/passport/login_sec" => Login(body!),
            "/v1/sms/send/verify_code" => Json(new { code = token == "tmp-token" ? 0 : 401 }),
            "/v1/app/trust_device/list" => Json(new { code = 0, data = new { list = Array.Empty<object>() } }),
            "/v1/app/trust_device/add" => TrustDevice(),
            "/v2/passport/profile" => token == _validToken && token is not null
                ? Json(new { code = 0, data = Encrypt("""{"email":"me@example.com"}""") })
                : new HttpResponseMessage(HttpStatusCode.Unauthorized),
            "/v2/house/station_list" => StationList(token),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private HttpResponseMessage Login(JsonNode body)
    {
        _clientPublicKey = body["client_secret_info"]!["public_key"]!.GetValue<string>();
        if (RequireCaptcha && body["captcha_id"] is null)
            return Json(new { code = 100032, msg = "need captcha", data = new { captcha_id = "cap-1", item = "data:image/png;base64,iVBORw0KGgo=" } });

        if (!TrustedDevice && body["verify_code"]?.GetValue<string>() != "123456")
            return Json(new { code = 26052, msg = "need verify code", data = new { auth_token = "tmp-token", token_expires_at = Expiry(1) } });

        _validToken = TrustedDevice && body["verify_code"] is null ? "relogin-token" : "final-token";
        return Json(new
        {
            code = 0,
            data = new
            {
                auth_token = _validToken,
                token_expires_at = Expiry(24 * 30),
                user_id = "uid-1",
                nick_name = "me",
                email = Encrypt("me@example.com"),
                server_secret_info = new { public_key = Server2Public },
            },
        });
    }

    private HttpResponseMessage TrustDevice()
    {
        TrustedDevice = true;
        return Json(new { code = 0 });
    }

    private HttpResponseMessage StationList(string? token)
    {
        if (FailNextStationListWith401)
        {
            FailNextStationListWith401 = false;
            _validToken = null;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }
        if (token != _validToken)
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        const string stations = """[{"station_sn":"T8010P0000000001","station_name":"HomeBase","device_type":0,"p2p_did":"EUFYCAM-123456-ABCDE","app_conn":"x","member":{"admin_user_id":42}}]""";
        return Json(new { code = 0, data = Encrypt(stations + "\0\0") });
    }

    /// <summary>Encrypts as the real server would: ECDH(server2 private, client public).</summary>
    private string Encrypt(string plain)
    {
        using var server2 = EufyCrypto.ImportKey(EufyCryptoTests.Server2Private, Server2Public);
        return EufyCrypto.EncryptApiData(plain, EufyCrypto.DeriveSharedSecret(server2, _clientPublicKey!));
    }

    private static long Expiry(int hours) => DateTimeOffset.UtcNow.AddHours(hours).ToUnixTimeSeconds();

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };
}
