using System.Text.RegularExpressions;

namespace Wintainium.Core.Install;

/// <summary>
/// Where a download named by the release goes (Get-SafeDownloadPath). Release data is untrusted
/// (GitLab link names and Gitea/Forgejo asset names are free text), so only the last part of
/// the name is kept and the result must stay inside the download folder.
/// </summary>
public static partial class DownloadPaths
{
    /// <summary>A plain file name inside <paramref name="dir"/>, or null when the name can't be used.</summary>
    public static string? GetSafe(string dir, string? name)
    {
        ArgumentException.ThrowIfNullOrEmpty(dir);
        string leaf = (name ?? "").Split('\\', '/')[^1];
        if (leaf.Length == 0 || leaf == "." || leaf == "..")
        {
            return null;
        }

        if (BadChars().IsMatch(leaf) || leaf.EndsWith('.') || leaf.EndsWith(' '))
        {
            return null;
        }

        // Windows device names, also with an extension or spaces before it (COM¹-³ and LPT¹-³ count too)
        if (DeviceName().IsMatch(leaf))
        {
            return null;
        }

        string baseDir = Path.GetFullPath(dir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(dir, leaf));
        return full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    [GeneratedRegex("[\\x00-\\x1f<>:\"|?*]")]
    private static partial Regex BadChars();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|(COM|LPT)[0-9¹²³])\s*(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceName();
}
