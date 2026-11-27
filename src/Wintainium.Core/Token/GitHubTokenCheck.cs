using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wintainium.Core.Token;

/// <summary>What GitHub said about a token. Ok = accepted; Safe = classic with no scopes.</summary>
public sealed record TokenCheckResult
{
    public bool Ok { get; init; }

    public bool Safe { get; init; }

    public int Limit { get; init; }

    public string Scopes { get; init; } = "";

    public bool FineGrained { get; init; }

    /// <summary>Expiry in local time, when GitHub reports one.</summary>
    public DateTime? Expires { get; init; }

    public string Message { get; init; } = "";
}

/// <summary>
/// Asks GitHub about a token without saving it (Test-GitHubToken). A classic token with no scopes
/// can't read private data or change anything; it only raises the lookup limit from 60 to 5,000.
/// </summary>
public static partial class GitHubTokenCheck
{
    public const string RateLimitUrl = "https://api.github.com/rate_limit";

    /// <summary>GitHub's page for a new classic token, with the description filled in and no scopes (T-08).</summary>
    public const string TokenPageUrl = "https://github.com/settings/tokens/new?description=Wintainium%20(read-only%2C%20no%20scopes)&scopes=";

    /// <summary>How long the check may take before it counts as a network problem.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public const string RejectedMessage = "GitHub did not accept this token. It may be mistyped, expired or deleted.";

    public const string NetworkMessage = "Could not reach GitHub to check the token. Check your internet connection and try again.";

    /// <summary>
    /// Checks a token. Throws <see cref="OperationCanceledException"/> only when <paramref name="cancel"/>
    /// was cancelled; a network failure or timeout is reported in the result, not blamed on the token.
    /// </summary>
    public static async Task<TokenCheckResult> CheckAsync(HttpClient http, string? token, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        string plain = (token ?? "").Trim();
        if (plain.Length == 0)
        {
            return new TokenCheckResult { Message = "No token entered." };
        }

        bool fine = plain.StartsWith("github_pat_", StringComparison.Ordinal);
        cancel.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(Timeout);
        HttpResponseMessage? resp = null;
        string body;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, RateLimitUrl);
            req.Headers.UserAgent.ParseAdd("Wintainium");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plain);
            resp = await http.SendAsync(req, timeout.Token).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                resp.Dispose();
                return new TokenCheckResult { FineGrained = fine, Message = RejectedMessage };
            }

            if (!resp.IsSuccessStatusCode)
            {
                int code = (int)resp.StatusCode;
                resp.Dispose();
                return new TokenCheckResult { FineGrained = fine, Message = $"GitHub could not check the token right now (HTTP {code}). Try again later." };
            }

            body = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            resp?.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            resp?.Dispose();
            return new TokenCheckResult { FineGrained = fine, Message = NetworkMessage };
        }

        using (resp)
        {

            int limit = 0;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("rate", out JsonElement rate) &&
                    rate.TryGetProperty("limit", out JsonElement l) && l.TryGetInt32(out int li))
                {
                    limit = li;
                }
            }
            catch (JsonException)
            {
                limit = 0;
            }

            string scopes = HeaderValue(resp, "X-OAuth-Scopes") is string s
                ? string.Join(", ", s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0))
                : "";
            DateTime? expires = HeaderValue(resp, "github-authentication-token-expiration") is string e ? ParseExpiry(e) : null;

            // A fine-grained token's permissions can't be read back from GitHub, so it can't be shown
            // to be harmless; ask for a classic token with no scopes instead (that one can be checked).
            bool safe = scopes.Length == 0 && !fine;
            string message = fine
                ? "This is a fine-grained token (github_pat_...). GitHub doesn't report what those are allowed to do, so Wintainium can't confirm it has no permissions. Click Open GitHub to make a classic token with every box unticked instead."
                : scopes.Length > 0
                    ? $"This token has permissions ({scopes}). Wintainium only needs a token with none. Make a new one with every box unticked."
                    : "Token accepted: no permissions, read-only public access.";

            return new TokenCheckResult
            {
                Ok = true,
                Safe = safe,
                Limit = limit,
                Scopes = scopes,
                FineGrained = fine,
                Expires = expires,
                Message = message,
            };
        }
    }

    /// <summary>
    /// GitHub sends e.g. "2027-01-14 13:46:22 -0500" or "2027-01-14 18:46:22 UTC". Returns local time,
    /// or null when the text doesn't look like that (ConvertFrom-GitHubExpiry).
    /// </summary>
    public static DateTime? ParseExpiry(string value)
    {
        Match m = ExpiryRegex().Match(value ?? "");
        if (!m.Success)
        {
            return null;
        }

        string zone = m.Groups[3].Value;
        if (zone.Length == 0 || zone.ToUpperInvariant() is "UTC" or "Z")
        {
            zone = "+00:00";
        }
        else if (!zone.Contains(':', StringComparison.Ordinal))
        {
            zone = zone[..3] + ":" + zone[3..];
        }

        if (DateTimeOffset.TryParse($"{m.Groups[1].Value}T{m.Groups[2].Value}{zone}", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dto))
        {
            return dto.LocalDateTime;
        }

        return null;
    }

    private static string? HeaderValue(HttpResponseMessage resp, string name)
    {
        if (resp.Headers.TryGetValues(name, out IEnumerable<string>? values) ||
            resp.Content.Headers.TryGetValues(name, out values))
        {
            return string.Join(",", values);
        }

        return null;
    }

    // PowerShell's -match is case-insensitive
    [GeneratedRegex(@"^\s*(\d{4}-\d{2}-\d{2})[ T](\d{2}:\d{2}:\d{2})\s*(UTC|Z|[+-]\d{2}:?\d{2})?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExpiryRegex();
}
