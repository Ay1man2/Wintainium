using System.Net;
using System.Text.RegularExpressions;
using Wintainium.Core.Data;

namespace Wintainium.Core.Releases;

/// <summary>Status texts shown in the grid (exactly as v0.3.0 shows them).</summary>
public static class Statuses
{
    public const string Update = "UPDATE";
    public const string UpToDate = "up to date";
    public const string TokenRejected = "token rejected";
    public const string RateLimited = "rate limited";
    public const string SiteBusy = "site busy";
    public const string LookupFailed = "lookup failed";
    public const string ExeNotFound = "exe not found";
    public const string CheckManually = "check manually";
}

/// <summary>
/// Turns a failed lookup into a status (Get-LookupStatus). Only GitHub lookups use the token, so
/// only they can be "token rejected" or "rate limited"; other sites that refuse or time out are
/// "site busy".
/// </summary>
public static partial class LookupStatus
{
    public static string For(Exception error, AppEntry app)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(app);
        var lx = error as LookupException;
        string why = $"{error.Message} {lx?.Body}";
        int code = lx?.Status is HttpStatusCode s ? (int)s : 0;
        if (code == 0)
        {
            Match m = CodeRegex().Match(why);
            if (m.Success)
            {
                code = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        if (string.Equals(app.Source, "github", StringComparison.OrdinalIgnoreCase))
        {
            if (code == 401 || why.Contains("Bad credentials", StringComparison.OrdinalIgnoreCase))
            {
                return Statuses.TokenRejected;
            }

            if (code is 403 or 429 || why.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            {
                return Statuses.RateLimited;
            }

            return Statuses.LookupFailed;
        }

        if (code is 403 or 429 or 502 or 503 or 504 || lx?.Unreachable == true || UnreachableRegex().IsMatch(why))
        {
            return Statuses.SiteBusy;
        }

        return Statuses.LookupFailed;
    }

    [GeneratedRegex(@"\b([45]\d\d)\b")]
    private static partial Regex CodeRegex();

    [GeneratedRegex("timed out|Unable to connect|could not be resolved|connection was closed", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnreachableRegex();
}
