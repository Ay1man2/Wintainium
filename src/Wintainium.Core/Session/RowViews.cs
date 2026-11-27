using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Wintainium.Core.Logging;
using Wintainium.Core.Releases;
using Wintainium.Core.Status;

namespace Wintainium.Core.Session;

/// <summary>The What's new window's texts (Show-Notes, Get-NotesText).</summary>
public sealed record NotesView(string Title, string Subtitle, string Body, string Page);

/// <summary>Texts and filters the window shows for rows, kept here so they are tested without a window.</summary>
public static partial class RowViews
{
    public const string NoNotesCached = "No release notes were published, or they are not cached yet (use Re-check all).";
    public const string NoReleaseInfo = "No release information for this app.";

    /// <summary>M-04 / M-05: search by name (case-insensitive) and "Only show updates".</summary>
    public static bool IsVisible(AppRow row, string? search, bool onlyUpdates)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (onlyUpdates && row.Status == Statuses.UpToDate)
        {
            return false;
        }

        string q = (search ?? "").Trim();
        return q.Length == 0 || row.App.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>G-03: title, "Released ... - you have ...", the notes with ** and leading #s removed.</summary>
    public static NotesView Notes(AppRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        string text = row.Notes.Length > 0 ? row.Notes : row.Latest.Length > 0 ? NoNotesCached : NoReleaseInfo;
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("**", "", StringComparison.Ordinal);
        text = Headings().Replace(text, "");
        string sub = row.Released.Length > 0 ? $"Released {row.Released}  -  you have {row.Installed}" : $"You have {row.Installed}";
        return new NotesView($"{row.App}  {row.Latest}", sub, text, row.Page);
    }

    /// <summary>
    /// L-02: the table --list prints (Show-Table), one line per row with its colour, then the
    /// number of updates.
    /// </summary>
    public static IReadOnlyList<(string Text, LogColor Color)> Table(IReadOnlyList<AppRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var lines = new List<(string, LogColor)>
        {
            ("", LogColor.Default),
            (Line("#", "App", "Status", "Installed", "Latest", "Released", "Update via"), LogColor.Cyan),
        };
        for (int i = 0; i < rows.Count; i++)
        {
            AppRow r = rows[i];
            LogColor color = r.Status switch
            {
                Statuses.Update => LogColor.Yellow,
                Statuses.UpToDate => LogColor.DarkGray,
                Statuses.CheckManually => LogColor.Gray,
                _ => LogColor.Red,
            };
            lines.Add((Line((i + 1).ToString(CultureInfo.InvariantCulture), Cut(r.App, 25), r.Status, Cut(r.Installed, 17), Cut(r.Latest, 25), r.Released, r.Via), color));
        }

        lines.Add(("", LogColor.Default));
        lines.Add(($"{rows.Count(r => r.Status == Statuses.Update)} update(s) available.", LogColor.Cyan));
        return lines;
    }

    /// <summary>' {0,2}  {1,-25} {2,-15} {3,-17} {4,-25} {5,-11} {6}'</summary>
    private static string Line(string n, string app, string status, string installed, string latest, string released, string via)
    {
        var sb = new StringBuilder();
        sb.Append(' ').Append(n.PadLeft(2)).Append("  ")
            .Append(app.PadRight(25)).Append(' ')
            .Append(status.PadRight(15)).Append(' ')
            .Append(installed.PadRight(17)).Append(' ')
            .Append(latest.PadRight(25)).Append(' ')
            .Append(released.PadRight(11)).Append(' ')
            .Append(via);
        return sb.ToString();
    }

    /// <summary>Cut: text longer than n becomes its first n-3 characters and "...".</summary>
    internal static string Cut(string s, int n) => s.Length <= n ? s : s[..(n - 3)] + "...";

    [GeneratedRegex(@"^#+\s*", RegexOptions.Multiline)]
    private static partial Regex Headings();
}
