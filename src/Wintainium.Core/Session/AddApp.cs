using System.Text.RegularExpressions;
using Wintainium.Core.Data;
using Wintainium.Core.Install;
using Wintainium.Core.Logging;
using Wintainium.Core.Releases;
using Wintainium.Core.Status;

namespace Wintainium.Core.Session;

/// <summary>Where a pasted link points: the fields a new entry starts with (A-01).</summary>
public sealed record AddSource(string Source, string Repo, string Url, string Api, string ShortName);

/// <summary>Add app: track an app from a release link, installing it first when asked (Add-App of v0.3.0).</summary>
public sealed partial class AddApp
{
    public const string UrlQuestion = "Repo or release URL (GitHub, GitLab, Gitea/Forgejo, or an itch.io game page)";

    /// <summary>Only files that could be a Windows build are offered when installing (A-06).</summary>
    public const string WindowsFiles = @"(?i)^(?!.*(linux|appimage|macos|darwin|android|\.sha256|\.zsync)).*\.(zip|7z|exe|msi)$";

    private readonly InstallContext ctx;
    private readonly Updater updater;
    private readonly AppList list;
    private readonly string installRoot;
    private readonly Func<AppEntry, CancellationToken, Task<ReleaseInfo?>> latestRelease;

    /// <summary>What "install it now" put on disk, so a cancelled or failed add can take it away again.</summary>
    private Installed? installed;

    private sealed record Installed(string Dir, string? TopCreated, IReadOnlyList<string> Files, string Download, IReadOnlySet<string> FoldersBefore);

    public AddApp(InstallContext ctx, Updater updater, AppList list, string installRoot, Func<AppEntry, CancellationToken, Task<ReleaseInfo?>> latestRelease)
    {
        this.ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        this.updater = updater ?? throw new ArgumentNullException(nameof(updater));
        this.list = list ?? throw new ArgumentNullException(nameof(list));
        this.installRoot = installRoot ?? throw new ArgumentNullException(nameof(installRoot));
        this.latestRelease = latestRelease ?? throw new ArgumentNullException(nameof(latestRelease));
    }

    /// <summary>
    /// GitHub (a link or owner/repo), an itch.io game page, a gitlab.com project, or any other host as
    /// Gitea/Forgejo. Null when the text isn't a link to a repository. User names and passwords in the
    /// link are dropped (X-05).
    /// </summary>
    public static AddSource? ParseSource(string? text)
    {
        string src = Links.StripUserInfo((text ?? "").Trim());
        Match m = GitHubLink().Match(src);
        if (!m.Success)
        {
            m = OwnerRepo().Match(src);
        }

        if (m.Success)
        {
            return new AddSource("github", $"{m.Groups[1].Value}/{m.Groups[2].Value}", "", "", m.Groups[2].Value);
        }

        m = ItchLink().Match(src);
        if (m.Success)
        {
            return new AddSource("itch", "", $"https://{m.Groups[1].Value}.itch.io/{m.Groups[2].Value}", "", m.Groups[2].Value);
        }

        m = GitLabLink().Match(src);
        if (m.Success)
        {
            string path = m.Groups[1].Value;
            return new AddSource("gitlab", "", "", $"https://gitlab.com/api/v4/projects/{Uri.EscapeDataString(path)}/releases/permalink/latest", path.Split('/')[^1]);
        }

        m = GiteaLink().Match(src);
        if (m.Success)
        {
            return new AddSource("gitea", "", "", $"https://{m.Groups[1].Value}/api/v1/repos/{m.Groups[2].Value}/{m.Groups[3].Value}/releases/latest", m.Groups[3].Value);
        }

        return null;
    }

    /// <summary>
    /// The file name pattern saved as "asset", so the next release's file is found: the downloaded
    /// name with its version number wildcarded (A-09).
    /// </summary>
    public static string AssetPattern(string fileName, string tag)
    {
        string pattern = "^" + Regex.Escape(fileName) + "$";
        string ver = VersionCompare.TagNumber(tag);
        return ver.Length > 0 ? pattern.Replace(Regex.Escape(ver), ".+", StringComparison.Ordinal) : pattern;
    }

    /// <summary>Asks for the link and the rest, then saves the new entry. Returns the entry, or null when nothing was added.</summary>
    public async Task<AppEntry?> RunAsync(CancellationToken cancel = default)
    {
        ILog log = ctx.Log;
        IUserPrompts ask = ctx.Prompts;
        log.Write("");
        string? url = ask.Ask("  " + UrlQuestion);
        if (url is null)
        {
            return Cancelled();
        }

        AddSource? src = ParseSource(url);
        if (src is null)
        {
            log.Write("  Cancelled: that does not look like a repo or release URL.", LogColor.Yellow);
            return null;
        }

        var def = new AppEntry { Name = "" };
        def.Source = src.Source;
        if (src.Repo.Length > 0)
        {
            def.Repo = src.Repo;
        }

        if (src.Url.Length > 0)
        {
            def.Url = src.Url;
        }

        if (src.Api.Length > 0)
        {
            def.Api = src.Api;
        }

        string? typedName = ask.Ask($"  App name (Enter = {src.ShortName})");
        if (typedName is null)
        {
            return Cancelled();
        }

        string name = typedName.Trim();
        if (name.Length == 0)
        {
            name = src.ShortName;
        }

        def.Name = name;
        if (src.Source == "github" && ask.Confirm("Track pre-releases / nightlies too?"))
        {
            def.Prerelease = true;
        }

        if (src.Source == "itch")
        {
            log.Write("  itch.io downloads go through its website, so this tracks a copy you already installed.");
            string? typedExe = ask.Ask("  Path to its .exe");
            if (typedExe is null)
            {
                return Cancelled();
            }

            string itchExe = Unquote(typedExe);
            if (itchExe.Length == 0)
            {
                log.Write("  Cancelled: an itch.io app needs the path to its .exe.", LogColor.Yellow);
                return null;
            }

            def.Exe = itchExe;
            def.Update = ["page"];
            Save(def);
            log.Write($"  Added {name}. Updates open its itch.io page.", LogColor.Green);
            return def;
        }

        string? typed = ask.Ask("  Path to its .exe (or a mod's folder) if it is already installed (Enter = install it now)");
        if (typed is null)
        {
            return Cancelled();
        }

        string exe = Unquote(typed);
        if (exe.Length > 0 && Directory.Exists(exe))
        {
            if (!await TrackFolderAsync(def, exe, cancel).ConfigureAwait(false))
            {
                return null;
            }
        }
        else if (exe.Length > 0)
        {
            def.Exe = exe;
            def.Update = ["download", "page"];
        }
        else
        {
            bool ok;
            try
            {
                ok = await InstallNowAsync(def, cancel).ConfigureAwait(false);
            }
            catch
            {
                RemoveInstalled();
                throw;
            }

            if (!ok)
            {
                RemoveInstalled();
                return null;
            }
        }

        try
        {
            Save(def);
        }
        catch
        {
            RemoveInstalled();
            throw;
        }

        installed = null;
        log.Write($"  Added {name}. Edit wintainium-apps.json to change how it updates.", LogColor.Green);
        return def;
    }

    /// <summary>A-05: an installed mod or data pack, tracked by release.</summary>
    private async Task<bool> TrackFolderAsync(AppEntry def, string folder, CancellationToken cancel)
    {
        ReleaseInfo? latest = await LatestAsync(def, cancel).ConfigureAwait(false);
        if (latest is null)
        {
            return false;
        }

        List<string> all = AllFiles(folder);
        if (all.Count == 0)
        {
            ctx.Log.Write("  That folder is empty, so I am not adding it.", LogColor.Red);
            return false;
        }

        def.Exe = FirstIps(all) ?? all[0];
        def.InstallDir = folder;
        bool same = ctx.Prompts.Confirm($"Is the latest release ({latest.Tag}) the one in that folder?", yesIsDefault: true);
        def.InstalledTag = same ? latest.Tag : "unknown";
        def.Update = ["zip", "page"];
        return true;
    }

    /// <summary>A-06 to A-09: downloads the latest release into a folder and works out what to track.</summary>
    private async Task<bool> InstallNowAsync(AppEntry def, CancellationToken cancel)
    {
        ILog log = ctx.Log;
        IUserPrompts ask = ctx.Prompts;
        ReleaseInfo? latest = await LatestAsync(def, cancel).ConfigureAwait(false);
        if (latest is null)
        {
            return false;
        }

        log.Write($"  Latest release: {latest.Tag}", LogColor.Cyan);
        string fallback = Path.Combine(installRoot, def.Name);
        string? typedDir = ask.Ask($"  Install folder (Enter = {fallback})");
        if (typedDir is null)
        {
            log.Write("  Cancelled.");
            return false;
        }

        string dir = Unquote(typedDir);
        if (dir.Length == 0)
        {
            dir = fallback;
        }

        if (File.Exists(dir))
        {
            log.Write($"  {dir} is a file, not a folder. Not adding {def.Name}.", LogColor.Red);
            return false;
        }

        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()
            && !ask.Confirm($"{dir} already has files in it. Install into it anyway?"))
        {
            log.Write("  Cancelled.");
            return false;
        }

        var pickEntry = new AppEntry { Name = def.Name, Asset = WindowsFiles };
        var pick = new AppRow
        {
            Entry = pickEntry,
            Status = "",
            Installed = "",
            Latest = latest.Tag,
            LatestTag = latest.Tag,
            Released = "",
            Methods = ["zip"],
            Page = latest.Page,
            Exe = "",
            Assets = latest.Assets,
        };
        string? file = await updater.GetAssetFileAsync(pick, cancel).ConfigureAwait(false);
        if (file is null)
        {
            return false;
        }

        // only files this add creates are taken away again; files already in the folder stay
        bool createdDir = !Directory.Exists(dir);
        var before = new HashSet<string>(createdDir ? [] : Directory.GetFiles(dir, "*", SearchOption.AllDirectories), StringComparer.OrdinalIgnoreCase);
        var foldersBefore = new HashSet<string>(createdDir ? [] : Directory.GetDirectories(dir, "*", SearchOption.AllDirectories), StringComparer.OrdinalIgnoreCase);
        // the highest folder this add creates, so parents it made are tidied up too
        string? topCreated = null;
        for (string? d = Path.GetFullPath(dir); d is not null && !Directory.Exists(d); d = Path.GetDirectoryName(d))
        {
            topCreated = d;
        }

        installed = new Installed(dir, topCreated, [], file, foldersBefore);
        string method;
        IReadOnlyList<string> written;
        if (Archives.CanUnpack(file, ctx.SevenZip))
        {
            UnpackResult? r = await Archives.ExpandAsync(file, dir, ctx, cancel: cancel).ConfigureAwait(false);
            written = r?.Files ?? [];
            method = "zip";
        }
        else
        {
            Directory.CreateDirectory(dir);
            string copy = Path.Combine(dir, Path.GetFileName(file));
            File.Copy(file, copy, overwrite: true);
            written = [copy];
            method = "download";
        }

        installed = installed with { Files = [.. written.Where(f => !before.Contains(f))] };

        log.Write($"  Installed into {dir}", LogColor.Green);

        // choose the file whose date/version tells us what is installed
        List<string> cands = Programs(dir);
        bool isData = false;
        if (cands.Count == 0)
        {
            List<string> all = AllFiles(dir);
            if (all.Count == 0)
            {
                log.Write("  Nothing was unpacked, so I am not adding it.", LogColor.Red);
                return false;
            }

            if (ask.Confirm("There is no .exe in it. Is it a mod or data pack, to track by release?", yesIsDefault: true))
            {
                // nothing to read a version from: remember the release instead, and one of its
                // files so a missing install still shows up
                isData = true;
                cands = [FirstIps(all) ?? all[0]];
            }
            else
            {
                cands = [.. Directory.GetFiles(dir).Order(StringComparer.OrdinalIgnoreCase)];
                if (cands.Count == 0)
                {
                    log.Write("  No program file at the top of that folder, so I am not adding it.", LogColor.Red);
                    return false;
                }
            }
        }

        string main = cands[0];
        if (cands.Count > 1)
        {
            int? n = ask.Choose("Which file is the main program?", [.. cands.Select(c => Path.GetRelativePath(dir, c))], "number (Enter = 1)");
            if (n is null)
            {
                log.Write("  Cancelled.");
                return false;
            }

            main = cands[n.Value];
        }

        def.Exe = main;
        await ctx.Delay(TimeSpan.FromSeconds(2), cancel).ConfigureAwait(false);
        if (!File.Exists(main))
        {
            await QuarantineHelp.ShowAsync(latest.Assets, Path.GetFileName(main), ctx, updater.Downloader, cancel).ConfigureAwait(false);
            log.Write($"  Not adding {def.Name}.", LogColor.Red);
            return false;
        }

        def.Asset = AssetPattern(Path.GetFileName(file), latest.Tag);
        def.Update = [method, "page"];
        if (isData)
        {
            def.InstallDir = dir;
            def.InstalledTag = latest.Tag;
            log.Write($"  Tracking it by release: {latest.Tag} is installed. Wintainium flags the next one.", LogColor.Cyan);
        }

        return true;
    }

    private async Task<ReleaseInfo?> LatestAsync(AppEntry def, CancellationToken cancel)
    {
        string why;
        try
        {
            ReleaseInfo? r = await latestRelease(def, cancel).ConfigureAwait(false);
            if (r is not null)
            {
                return r;
            }

            why = "no release was found.";
        }
        catch (Exception ex) when (ex is LookupException or HttpRequestException or IOException or System.Text.Json.JsonException or InvalidOperationException
                                   || (ex is OperationCanceledException && !cancel.IsCancellationRequested))
        {
            why = ex.Message;
        }

        ctx.Log.Write($"  Could not read the releases: {why}", LogColor.Red);
        return null;
    }

    /// <summary>Adds and saves; when saving fails the entry is taken out again, so a retry doesn't add it twice.</summary>
    private void Save(AppEntry def)
    {
        list.Add(def);
        try
        {
            list.Save();
        }
        catch
        {
            list.RemoveEntry(def);
            throw;
        }
    }

    private AppEntry? Cancelled()
    {
        ctx.Log.Write("  Cancelled.");
        return null;
    }

    /// <summary>
    /// A cancelled or failed add takes away what it installed: the files it unpacked or copied (never
    /// files that were already there), folders it made that are left empty, and the download.
    /// </summary>
    private void RemoveInstalled()
    {
        if (installed is not { } done)
        {
            return;
        }

        installed = null;
        int removed = 0;
        foreach (string f in done.Files.Append(done.Download))
        {
            try
            {
                if (File.Exists(f))
                {
                    File.Delete(f);
                    removed += f == done.Download ? 0 : 1;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ctx.Log.Write($"  Could not remove {f}: {ex.Message}", LogColor.Yellow);
            }
        }

        try
        {
            if (Directory.Exists(done.Dir))
            {
                foreach (string sub in Directory.GetDirectories(done.Dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                {
                    if (!done.FoldersBefore.Contains(sub) && !Directory.EnumerateFileSystemEntries(sub).Any())
                    {
                        Directory.Delete(sub);
                    }
                }

                if (done.TopCreated is not null)
                {
                    for (string? d = Path.GetFullPath(done.Dir); d is not null && Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any(); d = Path.GetDirectoryName(d))
                    {
                        Directory.Delete(d);
                        if (string.Equals(d, done.TopCreated, StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ctx.Log.Write($"  Could not tidy up {done.Dir}: {ex.Message}", LogColor.Yellow);
        }

        if (removed > 0)
        {
            ctx.Log.Write($"  Removed the {removed} file(s) it had installed.", LogColor.DarkGray);
        }
    }

    private static string Unquote(string answer) => (answer ?? "").Trim().Trim('"');

    private static string? FirstIps(List<string> files) =>
        files.FirstOrDefault(f => Path.GetExtension(f).Equals(".ips", StringComparison.OrdinalIgnoreCase));

    /// <summary>Every file below the folder, sorted by full path (Get-ChildItem -Recurse | Sort-Object FullName).</summary>
    private static List<string> AllFiles(string dir) =>
        [.. Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The .exe files below the folder: a folder's own files first, then each subfolder in name order.</summary>
    private static List<string> Programs(string dir)
    {
        var found = new List<string>();
        found.AddRange(Directory.GetFiles(dir, "*.exe").Order(StringComparer.OrdinalIgnoreCase));
        foreach (string sub in Directory.GetDirectories(dir).Order(StringComparer.OrdinalIgnoreCase))
        {
            found.AddRange(Programs(sub));
        }

        return found;
    }

    [GeneratedRegex(@"github\.com/([\w.-]+)/([\w.-]+?)(?:\.git)?(?:/.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubLink();

    [GeneratedRegex(@"^([\w.-]+)/([\w.-]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex OwnerRepo();

    [GeneratedRegex(@"^https?://([\w-]+)\.itch\.io/([\w-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ItchLink();

    [GeneratedRegex(@"^https?://gitlab\.com/(.+?)(?:\.git)?(?:/-/.*)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitLabLink();

    [GeneratedRegex(@"^https?://([^/]+)/([^/]+)/([^/]+?)(?:\.git)?(?:/.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GiteaLink();
}
