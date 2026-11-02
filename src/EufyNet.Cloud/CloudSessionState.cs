using System.Text.Json;

namespace EufyNet.Cloud;

/// <summary>
/// Everything that has to survive a restart so the app does not log in (and trigger 2FA) again.
/// Equivalent of the httpApi part of eufy-security-client's persistent.json.
/// Contains the auth token: keep the file private and out of source control.
/// </summary>
public sealed class CloudSessionState
{
    public string? ApiBase { get; set; }
    public string? Country { get; set; }
    /// <summary>md5("user:password"); a mismatch means the credentials changed and the session is dropped.</summary>
    public string? LoginHash { get; set; }

    public string? ClientPrivateKey { get; set; }
    public string? ClientPublicKey { get; set; }
    /// <summary>Key used to decrypt API data; the login response replaces the static one.</summary>
    public string? ServerPublicKey { get; set; }

    public string? Token { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
    public string? UserId { get; set; }
    public string? Email { get; set; }
    public string? NickName { get; set; }

    /// <summary>Fake phone identity sent in headers; must stay stable so the trusted device keeps matching.</summary>
    public string? OpenUdid { get; set; }
    public string? SerialNumber { get; set; }
    public string? PhoneModel { get; set; }

    public void ResetLogin()
    {
        ApiBase = null;
        Token = null;
        TokenExpiresAt = null;
        UserId = null;
        Email = null;
        NickName = null;
        ServerPublicKey = null;
    }
}

public interface ISessionStore
{
    Task<CloudSessionState?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CloudSessionState state, CancellationToken cancellationToken = default);
}

public sealed class JsonFileSessionStore(string path) : ISessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<CloudSessionState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<CloudSessionState>(stream, JsonOptions, cancellationToken);
    }

    public async Task SaveAsync(CloudSessionState state, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
            Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
        File.Move(temp, path, overwrite: true);
    }
}
