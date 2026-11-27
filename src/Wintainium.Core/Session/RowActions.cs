using System.Text.RegularExpressions;
using Wintainium.Core.Data;
using Wintainium.Core.Install;
using Wintainium.Core.Logging;
using Wintainium.Core.Releases;
using Wintainium.Core.Status;

namespace Wintainium.Core.Session;

/// <summary>What the toolbar and context menu do with one row (Start-App, Remove-AppEntry, ...).</summary>
public static partial class RowActions
{
    public const string SelectFirst = "Select an app in the list first.";

    /// <summary>R-05: the message when an action needs a row and none is selected.</summary>
    public static AppRow? Require(AppRow? row, ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (row is null)
        {
            log.Write(SelectFirst, LogColor.Yellow);
        }

        return row;
    }

    /// <summary>
    /// R-01: starts "launch" or the program in its own folder. A mod or data pack has nothing to
    /// run, so its folder opens instead.
    /// </summary>
    public static void Launch(AppRow row, IShell shell, ILog log)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(log);
        string start = row.Exe;
        if (row.Entry.Launch.Length > 0)
        {
            start = row.Entry.ExpandedLaunch;
        }
        else if (!Runnable().IsMatch(Path.GetExtension(start)))
        {
            string dir = ZipInstaller.AppFolder(row.Entry);
            if (Directory.Exists(dir))
            {
                log.Write($"  {row.App} has no program to start. Opening its folder.");
                shell.OpenFolder(dir);
            }
            else
            {
                log.Write($"  Folder not found: {dir}", LogColor.Red);
            }

            return;
        }

        if (!File.Exists(start) && !Directory.Exists(start))
        {
            log.Write($"  Not found: {start}", LogColor.Red);
            return;
        }

        log.Write($"  Starting {row.App}.");
        shell.Start(start, Path.GetDirectoryName(start) ?? "");
    }

    /// <summary>R-02: the app's folder in Explorer.</summary>
    public static void OpenFolder(AppRow row, IShell shell, ILog log, bool quietWhenMissing = false)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(log);
        string dir = ZipInstaller.AppFolder(row.Entry);
        if (Directory.Exists(dir))
        {
            shell.OpenFolder(dir);
        }
        else if (!quietWhenMissing)
        {
            log.Write($"Folder not found: {dir}", LogColor.Red);
        }
    }

    /// <summary>R-03: the release page, or a note that there is none.</summary>
    public static void OpenPage(AppRow row, IShell shell, ILog log, bool quietWhenMissing = false)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(log);
        if (row.Page.Length > 0)
        {
            WebPage.Open(row.Page, shell, log);
        }
        else if (!quietWhenMissing)
        {
            log.Write($"No release page is set for {row.App}.", LogColor.Yellow);
        }
    }

    /// <summary>R-04: asks first; removes the entry and its saved lookup. Files stay. True when removed.</summary>
    public static bool Remove(AppRow row, AppSession session, IUserPrompts prompts)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(prompts);
        if (!prompts.Confirm($"Remove {row.App} from the list? Its files stay where they are."))
        {
            return false;
        }

        session.List.Remove(row.App);
        session.List.Save();
        if (session.Cache.Remove(row.App))
        {
            session.Cache.Save();
        }

        session.Log.Write($"  Removed {row.App} from the list.", LogColor.Green);
        return true;
    }

    /// <summary>Update selected: an app that isn't marked UPDATE is only updated after asking.</summary>
    public static bool ConfirmUpdate(AppRow row, IUserPrompts prompts)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(prompts);
        return row.Status == Statuses.Update || prompts.Confirm($"{row.App} is {row.Status}. Run its update anyway?");
    }

    /// <summary>K-06: turns the gold self row on or off, saves it, and updates the rows at once.</summary>
    public static void SetGold(AppSession session, bool on)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Settings.Save("goldSelf", on);
        foreach (AppRow r in session.Rows.Where(r => r.Self))
        {
            r.Gold = on;
        }
    }

    [GeneratedRegex(@"^\.(exe|bat|cmd|lnk)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Runnable();
}
