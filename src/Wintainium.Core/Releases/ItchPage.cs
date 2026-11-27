using System.Globalization;
using System.Text.RegularExpressions;

namespace Wintainium.Core.Releases;

/// <summary>Reads an itch.io game page's download list (itch.io has no public release API).</summary>
public static partial class ItchPage
{
    /// <summary>
    /// Picks the upload matching <paramref name="assetPattern"/>, else the Windows one, else the first
    /// (Read-ItchPage). The tag is the upload's version, else its file name.
    /// </summary>
    public static ReleaseInfo Read(string html, string page, string assetPattern)
    {
        MatchCollection starts = NameRegex().Matches(html);
        if (starts.Count == 0)
        {
            throw new LookupException("no downloads listed on the itch.io page");
        }

        var uploads = new List<(string Name, string Version, DateTime? Date, bool Windows)>();
        for (int i = 0; i < starts.Count; i++)
        {
            int from = starts[i].Index;
            int to = i + 1 < starts.Count ? starts[i + 1].Index : Math.Min(html.Length, from + 4000);
            string chunk = html[from..to];
            Match v = VersionRegex().Match(chunk);
            DateTime? date = null;
            Match d = DateRegex().Match(chunk);
            if (d.Success && DateTime.TryParseExact(
                    d.Groups[1].Value,
                    "d MMMM yyyy @ H:mm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTime parsed))
            {
                date = parsed;
            }

            uploads.Add((starts[i].Groups[1].Value.Trim(), v.Success ? v.Groups[1].Value : "", date,
                chunk.Contains("icon-windows", StringComparison.OrdinalIgnoreCase)));
        }

        int pick = -1;
        if (!string.IsNullOrEmpty(assetPattern))
        {
            var rx = new Regex(assetPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            pick = uploads.FindIndex(u => rx.IsMatch(u.Name));
        }

        if (pick < 0)
        {
            pick = uploads.FindIndex(u => u.Windows);
        }

        if (pick < 0)
        {
            pick = 0;
        }

        // Names and versions are kept as the page has them, entities included, as v0.3.0 did:
        // the tag is compared with installedTag values that v0.3.0 saved.
        (string name, string version, DateTime? when, _) = uploads[pick];
        return new ReleaseInfo
        {
            Tag = version.Length > 0 ? version : name,
            Date = when,
            Page = page,
            Assets = [],
            Notes = $"Latest upload: {name}",
        };
    }

    [GeneratedRegex("class=\"name\">([^<]+)</strong>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"class=""version_name"">\s*Version\s+([^<]+?)\s*</span>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"<abbr title=""(\d{1,2} \w+ \d{4} @ \d{1,2}:\d{2}) UTC""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DateRegex();
}
