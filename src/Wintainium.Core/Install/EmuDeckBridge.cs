using System.Globalization;
using System.Text.RegularExpressions;
using Wintainium.Core.Data;
using Wintainium.Core.Logging;

namespace Wintainium.Core.Install;

/// <summary>Updates through EmuDeck's own installer functions (Invoke-EmuDeckInstall, Open-EmuDeck).</summary>
public static partial class EmuDeckBridge
{
    public static string AllScript(InstallContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return Path.Combine(ctx.EmuDeckData, "backend", "functions", "all.ps1");
    }

    /// <summary>EmuDeck install functions look like yuzu_install; anything else is refused (X-06).</summary>
    public static bool IsInstallFunction(string name) => FunctionName().IsMatch(name ?? "");

    public static void Open(IReadOnlyList<string> names, InstallContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ILog log = ctx.Log;
        log.Write("");
        log.Write($"EmuDeck manages: {string.Join(", ", names)}", LogColor.Cyan);
        log.Write("  In EmuDeck: Manage Emulators > click the emulator > ReInstall / Update. Repeat for each one listed.");
        log.Write("  Leave \"Reset configuration\" and \"Uninstall\" alone - those change settings or remove the emulator.");
        if (File.Exists(ctx.EmuDeckExe))
        {
            ctx.Shell.Start(ctx.EmuDeckExe, Path.GetDirectoryName(ctx.EmuDeckExe)!);
        }
        else
        {
            log.Write($"  EmuDeck not found at {ctx.EmuDeckExe} - open it yourself.", LogColor.Red);
        }
    }

    public static async Task InstallAsync(AppEntry app, InstallContext ctx, bool yes, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(ctx);
        ILog log = ctx.Log;
        string fn = app.EmudeckFn;
        string all = AllScript(ctx);
        if (fn.Length == 0 || !File.Exists(all))
        {
            Open([app.Name], ctx);
            return;
        }

        if (!IsInstallFunction(fn))
        {
            log.Write($"  \"{fn}\" isn't an EmuDeck install function (they look like yuzu_install), so it wasn't run. Check emudeckFn in wintainium-apps.json.", LogColor.Red);
            return;
        }

        string exe = app.ExpandedExe;
        if (ctx.Shell.IsRunning(Path.GetFileNameWithoutExtension(exe)))
        {
            log.Write($"  {app.Name} is running. Close it and try again.", LogColor.Red);
            return;
        }

        string dir = Path.GetDirectoryName(exe) ?? "";
        if (!yes)
        {
            log.Write($"  This runs EmuDeck's own installer function ({fn}).");
            log.Write($"  It downloads the build EmuDeck is set to use and copies it over {dir}");
            log.Write("  It does not run the configuration step, so settings, saves and mods stay as they are.");
            if (!ctx.Prompts.Confirm("Continue?"))
            {
                log.Write("  Cancelled.");
                return;
            }
        }

        log.Write($"  Updating {app.Name} through EmuDeck...", LogColor.Cyan);
        DateTime? before = File.Exists(exe) ? File.GetLastWriteTime(exe) : null;
        string tempDir = Path.Combine(ctx.EmuDeckData, "temp");
        DateTime started = ctx.Now();
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        string command = $". '{all.Replace("'", "''", StringComparison.Ordinal)}'; & {fn}";
        ProcessOutcome result = await ctx.Processes.RunAsync(
            powershell,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command],
            () => log.Progress(string.Format(CultureInfo.InvariantCulture, "  working... {0}s elapsed, {1} MB in EmuDeck's temp folder", (int)(ctx.Now() - started).TotalSeconds, FolderMegabytes(tempDir))),
            TimeSpan.FromSeconds(2),
            cancel).ConfigureAwait(false);
        if (result.Errors.Length > 0)
        {
            log.Write($"  EmuDeck reported: {result.Errors.Split('\n')[0].Trim()}", LogColor.Yellow);
        }

        DateTime? after = File.Exists(exe) ? File.GetLastWriteTime(exe) : null;
        if (after is DateTime a && after != before)
        {
            log.Write($"  Done. {Path.GetFileName(exe)} is now dated {a.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.", LogColor.Green);
        }
        else
        {
            log.Write("  Finished, but the exe did not change. EmuDeck may be pinned to this build, or the download failed.", LogColor.Yellow);
            log.Write($"  Log: {Path.Combine(ctx.EmuDeckData, "logs")}");
        }
    }

    private static long FolderMegabytes(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return 0;
            }

            long sum = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            return (long)Math.Round(sum / 1048576.0, MidpointRounding.ToEven);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9]+_install$")]
    private static partial Regex FunctionName();
}
