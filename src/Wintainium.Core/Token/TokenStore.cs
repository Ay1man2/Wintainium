using Wintainium.Core.Logging;

namespace Wintainium.Core.Token;

/// <summary>
/// The optional GitHub token: saved in wintainium-token.xml (DPAPI, this Windows user only), or
/// taken from the GITHUB_TOKEN environment variable when that one passes the same no-scope check.
/// </summary>
public sealed class TokenStore
{
    private readonly string path;
    private readonly ISecretProtector protector;
    private readonly Func<string?> readEnvironment;

    public TokenStore(string tokenFile, ISecretProtector protector, Func<string?>? readEnvironment = null)
    {
        path = tokenFile;
        this.protector = protector;
        this.readEnvironment = readEnvironment ?? (() => Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
    }

    public string FilePath => path;

    public bool HasSavedToken => File.Exists(path);

    /// <summary>Expiry of the token in use (local time), when known.</summary>
    public DateTime? Expires { get; private set; }

    /// <summary>The saved token, or null (with a warning when the file exists but can't be read, T-03).</summary>
    public StoredToken? ReadSaved(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            StoredToken t = TokenFile.Read(path, protector);
            Expires = t.Expires;
            return t;
        }
        catch (TokenFileException)
        {
            log.Write($"Could not read {path}. Click Setup to save the token again.", LogColor.Yellow);
            return null;
        }
    }

    /// <summary>Saves a token that was checked with <see cref="GitHubTokenCheck"/>.</summary>
    public void Save(string token, DateTime? expires)
    {
        TokenFile.Write(path, new StoredToken(token.Trim(), expires), protector);
        Expires = expires;
    }

    /// <summary>
    /// The token release lookups send to api.github.com, or null. GITHUB_TOKEN comes first, as in
    /// v0.3.0, but only when GitHub confirms it is a classic token with no scopes (X-04).
    /// </summary>
    public async Task<string?> GetLookupTokenAsync(HttpClient http, ILog log, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        string env = (readEnvironment() ?? "").Trim();
        if (env.Length > 0)
        {
            TokenCheckResult check = await GitHubTokenCheck.CheckAsync(http, env, cancel).ConfigureAwait(false);
            if (check.Ok && check.Safe)
            {
                Expires = check.Expires;
                return env;
            }

            log.Write("The GITHUB_TOKEN environment variable is set, but Wintainium doesn't use it: " + check.Message, LogColor.Yellow);
        }

        return ReadSaved(log)?.Token;
    }
}
