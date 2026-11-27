namespace Wintainium.Core.Releases;

/// <summary>Rules for links that come from release data (untrusted) or the app list.</summary>
public static class Links
{
    /// <summary>
    /// True for an absolute https link (Test-HttpsUrl). Release lookups, downloads and checksums
    /// only go over https; file:, UNC and http links are refused.
    /// </summary>
    public static bool IsHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? u) && u.Scheme == Uri.UriSchemeHttps;

    /// <summary>The parsed https link, or null.</summary>
    public static Uri? ParseHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? u) && u.Scheme == Uri.UriSchemeHttps ? u : null;

    /// <summary>Removes user:password@ from a link before it is shown or saved (X-05).</summary>
    public static string StripUserInfo(string url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? u) || u.UserInfo.Length == 0)
        {
            return url;
        }

        var b = new UriBuilder(u) { UserName = "", Password = "" };
        return b.Uri.AbsoluteUri;
    }
}
