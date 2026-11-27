using System.Text.RegularExpressions;
using Wintainium.Core.Releases;
using Wintainium.Core.Status;

namespace Wintainium.Core.Install;

/// <summary>Which file of a release to download (Select-Asset).</summary>
public static class AssetPicker
{
    public static ReleaseAsset? Select(AppRow row, IUserPrompts prompts)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(prompts);

        // Some projects host their downloads off GitHub; "downloadUrl" builds the link from the
        // release version, e.g. https://example.org/{version}/app-{version}.zip
        string downloadUrl = row.Entry.DownloadUrl;
        if (downloadUrl.Length > 0 && row.LatestTag.Length > 0)
        {
            string url = downloadUrl.Replace("{version}", VersionCompare.TagNumber(row.LatestTag), StringComparison.Ordinal);
            string path = Uri.TryCreate(url, UriKind.Absolute, out Uri? u) ? u.AbsolutePath : url;
            return new ReleaseAsset(Uri.UnescapeDataString(ReleaseSources.FileNameOf(path)), url);
        }

        var assets = row.Assets.Where(a => a.Url.Length > 0).ToList();
        string pattern = row.Entry.Asset;
        if (pattern.Length > 0)
        {
            assets = [.. assets.Where(a => Matches(a.Name, pattern))];
        }

        if (assets.Count == 0)
        {
            return null;
        }

        if (assets.Count == 1)
        {
            return assets[0];
        }

        int? pick = prompts.Choose("Which file?", [.. assets.Select(a => a.Name)], "number (Enter to cancel)");
        return pick is int i && i >= 0 && i < assets.Count ? assets[i] : null;
    }

    private static bool Matches(string name, string pattern)
    {
        try
        {
            return Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
