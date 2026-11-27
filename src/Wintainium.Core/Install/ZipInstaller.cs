using System.Globalization;
using System.Text.RegularExpressions;
using Wintainium.Core.Data;
using Wintainium.Core.Logging;

namespace Wintainium.Core.Install;

/// <summary>Zip updates with a backup first (Install-Zip, Get-AppFolder).</summary>
public static partial class ZipInstaller
{
    /// <summary>
    /// The folder an app lives in. Mods and data packs set "installDir", because the file they are
    /// tracked by can sit a few folders deep (e.g. &lt;mod&gt;\patches\&lt;file&gt;.ips).
    /// </summary>
    public static string AppFolder(AppEntry app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.InstallDir.Length > 0 ? app.ExpandedInstallDir : Path.GetDirectoryName(app.ExpandedExe) ?? "";
    }

    /// <summary>Returns true when the new files were unpacked.</summary>
    public static async Task<bool> InstallAsync(AppEntry app, string file, InstallContext ctx, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(ctx);
        ILog log = ctx.Log;
        string dir = AppFolder(app);
        string exe = app.ExpandedExe;
        if (!Archives.CanUnpack(file, ctx.SevenZip))
        {
            log.Write($"  I cannot unpack this file type here. It is saved at: {file}", LogColor.Yellow);
            ctx.Shell.ShowInExplorer(file);
            return false;
        }

        if (Path.GetExtension(exe).Equals(".exe", StringComparison.OrdinalIgnoreCase) && ctx.Shell.IsRunning(Path.GetFileNameWithoutExtension(exe)))
        {
            log.Write($"  {app.Name} is running. Close it and try again.", LogColor.Red);
            return false;
        }

        string backup = BackupName(dir, ctx.Now());
        log.Write($"  This will back up {dir}");
        log.Write($"                 to {backup}");
        log.Write("  and then unpack the new files over the original folder.");
        if (!ctx.Prompts.Confirm("Continue?"))
        {
            log.Write("  Cancelled.");
            return false;
        }

        bool hadFolder = Directory.Exists(dir);
        if (hadFolder)
        {
            Archives.CopyTree(dir, backup);
        }

        UnpackResult unpacked;
        try
        {
            string exeName = app.InstallDir.Length > 0 ? "" : Path.GetFileName(exe);
            unpacked = await Archives.ExpandAsync(file, dir, ctx, exeName, cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UnsafeArchiveException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            log.Write($"  {ex.Message}", LogColor.Red);
            log.Write($"  The backup is at {backup}");
            return false;
        }

        log.Write($"  Done. If anything is wrong, delete {dir} and rename the backup back.", LogColor.Green);
        if (app.Hint.Length > 0)
        {
            log.Write($"  Then: {app.Hint}");
        }

        PruneBackups(dir, ctx.BackupsToKeep, log);
        if (hadFolder)
        {
            ReportLeftovers(app, dir, Leftovers(dir, unpacked.Files), ctx);
        }

        return true;
    }

    /// <summary>Most leftover files listed by name; the rest are counted.</summary>
    public const int LeftoversListed = 15;

    /// <summary>
    /// Files in the app folder that the new release did not write: files an older release had,
    /// and the user's own saves or settings. They are never deleted (X-12), only reported.
    /// Paths are relative to <paramref name="dir"/>, sorted.
    /// </summary>
    public static IReadOnlyList<string> Leftovers(string dir, IEnumerable<string> written)
    {
        ArgumentNullException.ThrowIfNull(written);
        if (!Directory.Exists(dir))
        {
            return [];
        }

        var wrote = new HashSet<string>(written.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !wrote.Contains(Path.GetFullPath(f)))
            .Select(f => Path.GetRelativePath(dir, f))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ReportLeftovers(AppEntry app, string dir, IReadOnlyList<string> left, InstallContext ctx)
    {
        if (left.Count == 0)
        {
            return;
        }

        var text = new System.Text.StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{left.Count} file(s) in {dir} are not part of the new {app.Name} release.");
        text.AppendLine("They may be your own saves or settings, or files an older release had that it no longer needs.");
        text.AppendLine("Nothing was deleted. If you know they are not needed, you can delete them yourself.");
        text.AppendLine();
        foreach (string f in left.Take(LeftoversListed))
        {
            text.AppendLine("  " + f);
        }

        if (left.Count > LeftoversListed)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  ...and {left.Count - LeftoversListed} more");
        }

        text.AppendLine();
        text.Append("Open the folder now?");
        ctx.Log.Write($"  {left.Count} file(s) in {dir} are not in the new release. They were kept.", LogColor.Yellow);
        if (ctx.Prompts.Confirm(text.ToString()))
        {
            ctx.Shell.OpenFolder(dir);
        }
    }

    /// <summary>&lt;folder&gt;.backup-yyyyMMdd-HHmm, with -2, -3 ... when one already exists that minute.</summary>
    public static string BackupName(string dir, DateTime now)
    {
        string name = $"{dir.TrimEnd('\\', '/')}.backup-{now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}";
        string candidate = name;
        for (int n = 2; Directory.Exists(candidate) || File.Exists(candidate); n++)
        {
            candidate = $"{name}-{n}";
        }

        return candidate;
    }

    /// <summary>Keeps the newest <paramref name="keep"/> update backups of a folder and deletes older ones (X-12).</summary>
    public static IReadOnlyList<string> PruneBackups(string dir, int keep, ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        string trimmed = dir.TrimEnd('\\', '/');
        string? parent = Path.GetDirectoryName(trimmed);
        if (parent is null || !Directory.Exists(parent))
        {
            return [];
        }

        string leaf = Path.GetFileName(trimmed);
        var backups = Directory.GetDirectories(parent, leaf + ".backup-*")
            .Where(d => BackupSuffix().IsMatch(Path.GetFileName(d)[(leaf.Length + ".backup-".Length)..]))
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
            .ToList();
        var removed = new List<string>();
        foreach (string old in backups.Skip(Math.Max(keep, 1)))
        {
            Archives.TryDeleteTree(old);
            if (!Directory.Exists(old))
            {
                removed.Add(old);
                log.Write($"  Removed an older backup: {old}", LogColor.DarkGray);
            }
        }

        return removed;
    }

    [GeneratedRegex(@"^\d{8}-\d{4}(-\d+)?$")]
    private static partial Regex BackupSuffix();
}
