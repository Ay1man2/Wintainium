using System.Text.Json.Nodes;
using Wintainium.Core.Data;
using Wintainium.Core.Logging;
using Wintainium.Core.Scan;

namespace Wintainium.Core.Session;

/// <summary>One row of the Scan window: a candidate the user can tick, rename or point at its program.</summary>
public sealed class ScanPick
{
    public ScanPick(ScanCandidate candidate)
    {
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        Name = candidate.Name;
        Exe = candidate.Exe;
        Checked = candidate.Selected;
        Def = (JsonObject)candidate.Def.DeepClone();
    }

    public ScanCandidate Candidate { get; }

    /// <summary>The entry that is added; Locate fills its exe.</summary>
    public JsonObject Def { get; }

    public string Name { get; set; }

    public string Exe { get; private set; }

    public bool Checked { get; set; }

    /// <summary>A download whose installed program wasn't found yet (amber row, C-03).</summary>
    public bool NeedsLocate => Exe.Length == 0;

    /// <summary>C-03: the program picked with Locate .exe...; the row is ticked.</summary>
    public void Locate(string exe)
    {
        Exe = exe ?? throw new ArgumentNullException(nameof(exe));
        Def["exe"] = exe;
        Checked = true;
    }
}

/// <summary>What the Scan window says and does (Update-ScanSummary, Add-ScanSelection, Hide-ScanRow).</summary>
public static class ScanPicker
{
    public const string NothingNew = "Nothing new found: everything Wintainium recognizes is already in your list. You can still add apps one by one with Add app.";
    public const string NotFoundYet = "Installed program not found yet: click Locate .exe... to choose it, then it can be added.";

    /// <summary>C-06: the summary line; Good is true for the green "nothing new" text.</summary>
    public static (string Text, bool Good) Summary(IReadOnlyCollection<ScanPick> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return (NothingNew, true);
        }

        int needs = rows.Count(r => r.NeedsLocate && r.Candidate.Kind == CandidateKind.Download);
        string msg = $"Found {rows.Count} app{(rows.Count != 1 ? "s" : "")}.";
        if (needs > 0)
        {
            bool one = needs == 1;
            msg += $" {needs} {(one ? "was" : "were")} downloaded but not found installed: use Locate .exe to point at {(one ? "it" : "them")}, leave {(one ? "it" : "them")} unticked, or click Don't show again.";
        }

        return (msg, false);
    }

    /// <summary>C-07: the Add button's text.</summary>
    public static string AddLabel(int ticked) => ticked > 0 ? $"Add selected ({ticked})" : "Add selected";

    /// <summary>C-06 / C-05: the footer (mod folders found, extra folders scanned).</summary>
    public static string Footer(int modCount, IReadOnlyList<string> extraRoots)
    {
        ArgumentNullException.ThrowIfNull(extraRoots);
        var lines = new List<string>();
        if (modCount > 0)
        {
            lines.Add($"Also found {modCount} mod or texture-pack folder(s) from code-hosting sites. To track one, use Add app with the mod's link and choose its folder.");
        }

        if (extraRoots.Count > 0)
        {
            lines.Add("Also scanned: " + string.Join(", ", extraRoots));
        }

        return string.Join("\n", lines);
    }

    /// <summary>The note under a row that needs Locate, without its own "click Locate" ending.</summary>
    public static string LocateNote(string note) =>
        (note ?? "").EndsWith("; click Locate to pick the installed .exe", StringComparison.Ordinal)
            ? note![..^"; click Locate to pick the installed .exe".Length]
            : note ?? "";

    /// <summary>
    /// C-07: adds every ticked row that has a program, with unique names ("Name (2)"), saves the
    /// list once, and logs it. Returns how many were added. When saving fails, the new entries are
    /// taken out again and the error is thrown, so trying again doesn't add them twice.
    /// </summary>
    public static int AddSelected(IEnumerable<ScanPick> rows, AppList list, ILog log)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(log);
        var added = new List<AppEntry>();
        foreach (ScanPick r in rows)
        {
            if (!r.Checked || r.NeedsLocate)
            {
                continue;
            }

            string name = r.Name.Trim();
            if (name.Length == 0)
            {
                name = r.Candidate.Name;
            }

            var def = (JsonObject)r.Def.DeepClone();
            def["name"] = list.UniqueName(name);
            var entry = new AppEntry(def);
            list.Add(entry);
            added.Add(entry);
        }

        if (added.Count > 0)
        {
            try
            {
                list.Save();
            }
            catch
            {
                foreach (AppEntry e in added)
                {
                    list.RemoveEntry(e);
                }

                throw;
            }

            log.Write($"Added {added.Count} app{(added.Count != 1 ? "s" : "")} from the scan.", LogColor.Green);
        }

        return added.Count;
    }

    /// <summary>C-04: Don't show again, saved in scanIgnore.</summary>
    public static void Hide(ScanPick row, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(settings);
        var keys = settings.ScanIgnore.Where(k => k.Length > 0).ToList();
        if (row.Candidate.Key.Length > 0 && !keys.Contains(row.Candidate.Key, StringComparer.OrdinalIgnoreCase))
        {
            keys.Add(row.Candidate.Key);
        }

        settings.Save("scanIgnore", new JsonArray([.. keys.Select(k => (JsonNode)JsonValue.Create(k))]));
    }

    /// <summary>C-04: Show hidden forgets every hidden item.</summary>
    public static void ShowHidden(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Save("scanIgnore", new JsonArray());
    }
}
