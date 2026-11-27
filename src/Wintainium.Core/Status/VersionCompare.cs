using System.Text.RegularExpressions;

namespace Wintainium.Core.Status;

/// <summary>Comparing the installed program's version with a release tag.</summary>
public static partial class VersionCompare
{
    /// <summary>The release tag without its leading non-digits ("v2.9.181" -> "2.9.181").</summary>
    public static string TagNumber(string tag) => LeadingNonDigits().Replace(tag ?? "", "");

    /// <summary>
    /// True when the installed version is that release (X-10). v0.3.0 used a substring test,
    /// where 12.1.0 "contained" 2.1. Now the release number must stand on its own in the installed
    /// version: "2.9.181", "2.9.181.0" and "2.9.181-qt" match 2.9.181; "12.1.0" and "1.2.1" don't
    /// match 2.1.
    /// </summary>
    public static bool IsSameRelease(string installed, string tagNumber)
    {
        if (string.IsNullOrEmpty(installed) || string.IsNullOrEmpty(tagNumber))
        {
            return false;
        }

        if (string.Equals(installed, tagNumber, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rx = new Regex(@"(?<![\d.])" + Regex.Escape(tagNumber) + @"(?:\.0+)*(?!\d|\.\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return rx.IsMatch(installed);
    }

    /// <summary>
    /// True if the installed version is older than the release, false if not, null when the two
    /// can't be compared and the release date decides instead (Test-OlderVersion).
    /// Same numbering scheme (same major, and for x.y.z the same minor too): the numbers decide.
    /// Different scheme (X-09): v0.3.0 always fell back to the date, so 2.9.181 -> 3.0.0 could
    /// show "up to date". Now a real installed version (not a placeholder like 1.0.0.0) that is
    /// lower than the release counts as older; anything else still falls back to the date.
    /// </summary>
    public static bool? IsOlder(string installed, string release)
    {
        string a = VersionPrefix().Match(installed ?? "").Value;
        string b = VersionPrefix().Match(release ?? "").Value;
        if (a.Length == 0 || b.Length == 0)
        {
            return null;
        }

        if (!Version.TryParse(a, out Version? va) || !Version.TryParse(b, out Version? vb))
        {
            return null;
        }

        Version pa = Pad(va);
        Version pb = Pad(vb);
        bool sameScheme = va.Major == vb.Major && !(va.Build >= 0 && vb.Build >= 0 && va.Minor != vb.Minor);
        if (sameScheme)
        {
            return pa < pb;
        }

        bool placeholder = va.Minor == 0 && Math.Max(va.Build, 0) == 0 && Math.Max(va.Revision, 0) == 0;
        if (!placeholder && pa < pb)
        {
            return true;
        }

        return null;
    }

    /// <summary>The comparison v0.3.0 made, kept so tests can show exactly where v0.4.0 differs.</summary>
    internal static bool? IsOlderV030(string installed, string release)
    {
        string a = VersionPrefix().Match(installed ?? "").Value;
        string b = VersionPrefix().Match(release ?? "").Value;
        if (a.Length == 0 || b.Length == 0 || !Version.TryParse(a, out Version? va) || !Version.TryParse(b, out Version? vb))
        {
            return null;
        }

        if (va.Major != vb.Major || (va.Build >= 0 && vb.Build >= 0 && va.Minor != vb.Minor))
        {
            return null;
        }

        return Pad(va) < Pad(vb);
    }

    private static Version Pad(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    [GeneratedRegex(@"^\d+(\.\d+){1,3}")]
    private static partial Regex VersionPrefix();

    [GeneratedRegex(@"^[^\d]*")]
    private static partial Regex LeadingNonDigits();
}
