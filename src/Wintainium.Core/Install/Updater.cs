using Wintainium.Core.Data;
using Wintainium.Core.Logging;
using Wintainium.Core.Releases;
using Wintainium.Core.Status;

namespace Wintainium.Core.Install;

/// <summary>Updating apps by their update methods (Update-App, Invoke-Method, Get-AssetFile, Update-All).</summary>
public sealed class Updater
{
    private readonly InstallContext ctx;
    private readonly AppList? list;
    private readonly Downloader downloader;

    /// <param name="list">The app list, so mods can record the release now installed.</param>
    public Updater(InstallContext ctx, AppList? list)
    {
        this.ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        this.list = list;
        downloader = new Downloader(ctx);
    }

    public Downloader Downloader => downloader;

    /// <summary>Asks which method when there are several (Enter = the first), then runs it.</summary>
    public async Task UpdateAppAsync(AppRow row, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        ctx.Log.Write("");
        ctx.Log.Write($"{row.App}: installed {row.Installed}, latest {row.Latest} ({row.Released})", LogColor.Cyan);
        string method = row.Methods[0];
        if (row.Methods.Count > 1)
        {
            int? pick = ctx.Prompts.Choose($"How do you want to update {row.App}?", [.. row.Methods.Select(StatusEngine.MethodLabel)], "how? (Enter = 1, x = cancel)");
            if (pick is not int i || i < 0 || i >= row.Methods.Count)
            {
                return;
            }

            method = row.Methods[i];
        }

        await InvokeMethodAsync(row, method, yes: false, cancel).ConfigureAwait(false);
    }

    /// <summary>Every outdated app: Yes / No / Stop each, using its first method and skipping the confirmations inside.</summary>
    public async Task UpdateAllAsync(IReadOnlyList<AppRow> rows, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var todo = rows.Where(r => r.Status == Statuses.Update).ToList();
        if (todo.Count == 0)
        {
            ctx.Log.Write("Nothing to update.");
            return;
        }

        foreach (AppRow row in todo)
        {
            string method = row.Methods[0];
            ctx.Log.Write("");
            YesNoStop ans = ctx.Prompts.AskYesNoStop($"Update {row.App} via {StatusEngine.MethodLabel(method)}?");
            if (ans == YesNoStop.Stop)
            {
                break;
            }

            if (ans == YesNoStop.Yes)
            {
                await InvokeMethodAsync(row, method, yes: true, cancel).ConfigureAwait(false);
            }
        }
    }

    public async Task InvokeMethodAsync(AppRow row, string method, bool yes, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        ILog log = ctx.Log;
        AppEntry app = row.Entry;
        switch (method?.ToLowerInvariant())
        {
            case "emudeck":
                await EmuDeckBridge.InstallAsync(app, ctx, yes, cancel).ConfigureAwait(false);
                break;
            case "inapp":
            {
                log.Write($"  Starting {row.App}.", LogColor.Cyan);
                if (app.Hint.Length > 0)
                {
                    log.Write($"  Then: {app.Hint}");
                }

                // "launch" overrides the exe when the updater lives in a launcher or script
                string start = app.Launch.Length > 0 ? app.ExpandedLaunch : row.Exe;
                if (File.Exists(start))
                {
                    ctx.Shell.Start(start, Path.GetDirectoryName(start) ?? "");
                }
                else
                {
                    log.Write($"  Not found: {start}", LogColor.Red);
                }

                break;
            }

            case "installer":
            {
                string? file = await GetAssetFileAsync(row, cancel).ConfigureAwait(false);
                if (file is not null)
                {
                    log.Write($"  Saved to {file}");
                    if (ctx.Prompts.Confirm("Run the installer now?"))
                    {
                        ctx.Shell.Start(file, Path.GetDirectoryName(file) ?? "");
                    }
                }

                break;
            }

            case "zip":
            {
                string? file = await GetAssetFileAsync(row, cancel).ConfigureAwait(false);
                if (file is not null && await ZipInstaller.InstallAsync(app, file, ctx, cancel).ConfigureAwait(false) && app.HasInstalledTag)
                {
                    SaveInstalledTag(row);
                }

                break;
            }

            case "download":
            {
                string? file = await GetAssetFileAsync(row, cancel).ConfigureAwait(false);
                if (file is not null)
                {
                    log.Write($"  Saved to {file}");
                    ctx.Shell.ShowInExplorer(file);
                }

                break;
            }

            default:
                if (row.Page.Length > 0)
                {
                    WebPage.Open(row.Page, ctx.Shell, log);
                }
                else
                {
                    log.Write("  No release page is set for this app. Add its repo or url to wintainium-apps.json.", LogColor.Yellow);
                }

                break;
        }
    }

    /// <summary>Picks, downloads and checks the release file. Null when there is nothing safe to use.</summary>
    public async Task<string?> GetAssetFileAsync(AppRow row, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        ILog log = ctx.Log;
        ReleaseAsset? asset = AssetPicker.Select(row, ctx.Prompts);
        if (asset is null)
        {
            log.Write("  No matching download on that release. Opening the release page instead.", LogColor.Yellow);
            if (row.Page.Length > 0)
            {
                WebPage.Open(row.Page, ctx.Shell, log);
            }

            return null;
        }

        string? file = DownloadPaths.GetSafe(ctx.DownloadDir, asset.Name);
        if (file is null)
        {
            log.Write($"  Not downloading '{asset.Name}': the release gives it an unsafe file name.", LogColor.Red);
            return null;
        }

        Directory.CreateDirectory(ctx.DownloadDir);
        DownloadOutcome got = await downloader.SaveAsync(asset.Url, file, Path.GetFileName(file), cancel).ConfigureAwait(false);
        if (got == DownloadOutcome.Removed)
        {
            await QuarantineHelp.ShowAsync(row.Assets, asset.Name, ctx, downloader, cancel).ConfigureAwait(false);
            return null;
        }

        if (got != DownloadOutcome.Ok)
        {
            return null;
        }

        ChecksumOutcome check = await Checksums.VerifyAsync(row.Assets, asset.Name, file, ctx, downloader, cancel).ConfigureAwait(false);
        if (check == ChecksumOutcome.Removed)
        {
            await QuarantineHelp.ShowAsync(row.Assets, asset.Name, ctx, downloader, cancel).ConfigureAwait(false);
            return null;
        }

        if (check != ChecksumOutcome.Ok)
        {
            Downloader.TryDelete(file);
            return null;
        }

        return file;
    }

    /// <summary>Records the release now installed, for apps tracked by release tag (mods, data packs).</summary>
    private void SaveInstalledTag(AppRow row)
    {
        AppEntry? app = list?.Find(row.App);
        if (app is null || row.LatestTag.Length == 0)
        {
            return;
        }

        // the tag itself: v0.3.0 saved the shown text, which could end in " (cached)"
        app.InstalledTag = row.LatestTag;
        list!.Save();
    }
}
