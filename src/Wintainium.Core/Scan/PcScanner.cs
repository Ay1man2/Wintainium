using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Wintainium.Core.Data;
using Wintainium.Core.Json;
using Wintainium.Core.Status;

namespace Wintainium.Core.Scan;

/// <summary>
/// Read-only scan for what on this PC came from GitHub (or GitLab, Codeberg, Forgejo, itch.io
/// and similar), so nothing is missing from the list (Find-Sources -PassThru). Nothing is
/// changed, moved or deleted. Git clones are not searched: the Scan window never used them.
/// </summary>
public sealed partial class PcScanner
{
    public const string SelfRepo = "Ay1man2/Wintainium";

    public const string InstalledSelfHint = "Run the new Wintainium.exe when asked, close Wintainium when it says so, and it reopens updated. Your list, settings and token are kept.";
    public const string PortableSelfHint = "Close Wintainium, then unpack the new portable zip over this folder. Your list, settings and token are kept.";

    private readonly ScanOptions o;
    private readonly List<ScanRow> rows = [];
    private readonly HashSet<string> trackedRepos = new(StringComparer.Ordinal);
    private readonly HashSet<string> trackedUrls = new(StringComparer.Ordinal);
    private readonly HashSet<string> trackedExes = new(StringComparer.Ordinal);
    private readonly List<string> trackedDirs = [];
    private readonly Dictionary<string, RepoInfo> archiveOrigin = new(StringComparer.Ordinal);

    public PcScanner(ScanOptions options)
    {
        o = options ?? throw new ArgumentNullException(nameof(options));
        foreach (AppEntry t in o.Tracked)
        {
            if (t.Repo.Length > 0)
            {
                trackedRepos.Add(t.Repo.ToLowerInvariant());
            }

            string? fromApi = RepoFromApi(t.Api);
            if (fromApi is not null)
            {
                trackedRepos.Add(fromApi.ToLowerInvariant());
            }

            if (t.Url.Length > 0)
            {
                trackedUrls.Add(t.Url.TrimEnd('/').ToLowerInvariant());
            }

            if (t.Exe.Length > 0)
            {
                string exe = o.Expand(t.Exe);
                trackedExes.Add(Key(exe));
                trackedDirs.Add(Key(Parent(exe)));
            }
        }
    }

    /// <summary>Runs the scan. Slow on big folders: call it off the UI thread.</summary>
    public ScanResult Run(CancellationToken cancel = default)
    {
        ScanKnown();
        ScanSelf();
        ScanDownloads(cancel);
        ScanApps(cancel);
        int mods = ScanMods(cancel);
        (List<ScanCandidate> candidates, int hidden) = Candidates();
        string[] roots = [.. o.AppRoots.Concat(o.DownloadRoots).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
        return new ScanResult(candidates, hidden, mods, rows, roots);
    }

    /// <summary>A ready-to-add entry for something found (New-AppDef).</summary>
    public static JsonObject NewAppDef(string name, RepoInfo info, string exe)
    {
        ArgumentNullException.ThrowIfNull(info);
        var d = new JsonObject { ["name"] = name, ["exe"] = exe };
        switch (info.Site)
        {
            case "itch.io":
                d["source"] = "itch";
                d["url"] = info.Url;
                d["update"] = new JsonArray("page");
                break;
            case "github.com":
                d["source"] = "github";
                d["repo"] = info.Repo;
                d["update"] = new JsonArray("download", "page");
                break;
            case "gitlab.com":
                d["source"] = "gitlab";
                d["api"] = $"https://gitlab.com/api/v4/projects/{Uri.EscapeDataString(info.Repo)}/releases/permalink/latest";
                d["update"] = new JsonArray("download", "page");
                break;
            default:
                // Codeberg, Forgejo and Gitea share one release API
                d["source"] = "gitea";
                d["api"] = $"https://{info.Site}/api/v1/repos/{info.Repo}/releases/latest";
                d["update"] = new JsonArray("download", "page");
                break;
        }

        return d;
    }

    private static string Key(string path) => path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

    private static string Parent(string path) => Path.GetDirectoryName(path.Replace('\\', Path.DirectorySeparatorChar)) ?? "";

    /// <summary>Catalog paths use backslashes; on another OS (tests) they become that OS's separator.</summary>
    private static string Native(string path) => Path.DirectorySeparatorChar == '\\' ? path : path.Replace('\\', Path.DirectorySeparatorChar);

    private static string? RepoFromApi(string api)
    {
        Match m = ReposApi().Match(api ?? "");
        if (m.Success)
        {
            return m.Groups[1].Value;
        }

        m = ProjectsApi().Match(api ?? "");
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
    }

    private bool IsTrackedRepo(string repo) => repo.Length > 0 && trackedRepos.Contains(repo.ToLowerInvariant());

    private bool IsTrackedUrl(string url) => url.Length > 0 && trackedUrls.Contains(url.TrimEnd('/').ToLowerInvariant());

    private bool IsTrackedDir(string dir)
    {
        string d = Key(dir);
        return trackedDirs.Exists(t => d == t || d.StartsWith(t + "\\", StringComparison.Ordinal) || t.StartsWith(d + "\\", StringComparison.Ordinal));
    }

    private (string Host, string Referrer)? Origin(string path)
    {
        string? z = o.Marks.Read(path);
        if (z is null)
        {
            return null;
        }

        string host = "";
        string referrer = "";
        foreach (string line in z.Split('\n'))
        {
            string l = line.TrimEnd('\r');
            if (l.StartsWith("HostUrl=", StringComparison.OrdinalIgnoreCase))
            {
                host = l["HostUrl=".Length..].Trim();
            }
            else if (l.StartsWith("ReferrerUrl=", StringComparison.OrdinalIgnoreCase))
            {
                referrer = l["ReferrerUrl=".Length..].Trim();
            }
        }

        return (host, referrer);
    }

    /// <summary>Where a file came from: its own download mark, or the archive it was unpacked from (Resolve-Origin).</summary>
    private RepoInfo? ResolveOrigin(string path)
    {
        if (Origin(path) is not (string host, string referrer))
        {
            return null;
        }

        RepoInfo? info = SourceUrls.Parse(referrer, o.RepoIds);
        if (info is null || (info.Repo.Length == 0 && info.Url.Length == 0))
        {
            RepoInfo? h = SourceUrls.Parse(host, o.RepoIds);
            if (h is not null && (h.Repo.Length > 0 || h.Url.Length > 0 || info is null))
            {
                info = h;
            }
        }

        if (info is null && referrer.Length > 0 && archiveOrigin.TryGetValue(referrer.ToLowerInvariant(), out RepoInfo? a))
        {
            info = a;
        }

        return info;
    }

    // 0. Apps Wintainium recognizes at their usual paths
    private void ScanKnown()
    {
        foreach (JsonObject k in o.Catalog)
        {
            (string Raw, string Full)? hit = null;
            foreach (string cand in k.GetStringList("exe"))
            {
                string raw = cand.Replace("{EmulationPath}", o.EmulationPath, StringComparison.Ordinal);
                string full = o.Expand(raw);
                if (File.Exists(Native(full)))
                {
                    hit = (raw, full);
                    break;
                }
            }

            if (hit is not (string rawPath, string fullPath))
            {
                continue;
            }

            string repo = k.GetString("repo");
            string repoKey = repo.Length > 0 ? repo : RepoFromApi(k.GetString("api")) ?? "";
            string name = k.GetString("name");
            bool isTracked = trackedExes.Contains(Key(fullPath)) || IsTrackedRepo(repoKey)
                || o.Tracked.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            var def = k.DeepClone().AsObject();
            def.SetField("exe", rawPath);
            var methods = k.GetStringList("update").Where(m => !m.Equals("emudeck", StringComparison.OrdinalIgnoreCase) || o.EmuDeckBackend).ToList();
            if (methods.Count == 0)
            {
                methods.Add("page");
            }

            def.SetField("update", new JsonArray([.. methods.Select(m => (JsonNode)m)]));
            if (!methods.Contains("emudeck", StringComparer.OrdinalIgnoreCase))
            {
                def.RemoveField("emudeckFn");
            }

            string source = repo.Length > 0 ? $"{repo} (GitHub)"
                : k.GetString("source") == "gitlab" ? $"{repoKey} (GitLab)"
                : k.GetString("source");
            rows.Add(new ScanRow(ScanKind.Known, name, source, repoKey, isTracked, fullPath, "recognized at its usual install path", "", def));
        }
    }

    // Wintainium itself, so users hear about new Wintainium releases too
    private void ScanSelf()
    {
        if (IsTrackedRepo(SelfRepo) || o.SelfFolder.Length == 0)
        {
            return;
        }

        string exe = Path.Combine(o.SelfFolder, "Wintainium.exe");
        if (!File.Exists(exe))
        {
            return;
        }

        JsonObject def = File.Exists(Path.Combine(o.SelfFolder, DataFolder.InstalledVersionFileName))
            // installed: update by downloading the new Wintainium.exe (checked against its
            // published SHA-256) and running it; it asks you to close Wintainium first
            ? new JsonObject { ["name"] = "Wintainium", ["exe"] = exe, ["source"] = "github", ["repo"] = SelfRepo, ["update"] = new JsonArray("installer", "page"), ["asset"] = "^Wintainium\\.exe$", ["hint"] = InstalledSelfHint }
            : new JsonObject { ["name"] = "Wintainium", ["exe"] = exe, ["source"] = "github", ["repo"] = SelfRepo, ["update"] = new JsonArray("page"), ["hint"] = PortableSelfHint };
        rows.Add(new ScanRow(ScanKind.Known, "Wintainium", $"{SelfRepo} (GitHub)", SelfRepo, false, exe, "this app", "", def));
    }

    // 1. Downloaded files and where they came from
    private void ScanDownloads(CancellationToken cancel)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in o.DownloadRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string f in Walk.Files(root, 6, cancel).Where(f => DownloadTypes().IsMatch(Path.GetFileName(f))))
            {
                if (!seen.Add(f) || Origin(f) is not (string host, string referrer))
                {
                    continue;
                }

                RepoInfo? info = SourceUrls.Parse(referrer, o.RepoIds);
                if (info is null || info.Repo.Length == 0)
                {
                    RepoInfo? h = SourceUrls.Parse(host, o.RepoIds);
                    if (h is not null && (h.Repo.Length > 0 || info is null))
                    {
                        info = h;
                    }
                }

                if (info is null)
                {
                    // from a website, not a code host: only v0.3.0's console report listed these
                    continue;
                }

                archiveOrigin[f.ToLowerInvariant()] = info;
                string detail = info.Repo.Length > 0 || info.Url.Length > 0 ? "" : "repo not recorded; see the URL in the Zone.Identifier";
                bool tracked = IsTrackedRepo(info.Repo) || IsTrackedUrl(info.Url);
                rows.Add(new ScanRow(ScanKind.Download, Path.GetFileName(f), info.Site, info.Repo, tracked, f, detail, info.Url));
            }
        }
    }

    // 3. Programs in app folders that aren't tracked
    private void ScanApps(CancellationToken cancel)
    {
        foreach (string root in o.AppRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (root.Length == 0 || !Directory.Exists(root))
            {
                continue;
            }

            // one row per app folder: the folders directly under the root, or the root itself for loose programs
            var folders = Walk.Dirs(root).Where(d => !Path.GetFileName(d).Contains(".backup-", StringComparison.OrdinalIgnoreCase)).ToList();
            bool rootIsDownloads = string.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(o.DownloadsFolder.Length > 0 ? o.DownloadsFolder : "\0none").TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            foreach (string dir in folders)
            {
                cancel.ThrowIfCancellationRequested();
                if (IsTrackedDir(dir))
                {
                    continue;
                }

                string? main = Programs(dir, 2, cancel).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
                if (main is not null)
                {
                    AddApp(Path.GetFileName(dir), main);
                }
            }

            // loose files in Downloads are downloads (installers, archives), not installed apps
            if (!rootIsDownloads)
            {
                foreach (string e in Programs(root, 0, cancel))
                {
                    // loose programs share a folder (like tools), so only an exact match counts as tracked
                    if (!trackedExes.Contains(Key(e)))
                    {
                        AddApp(Path.GetFileNameWithoutExtension(e), e);
                    }
                }
            }
        }
    }

    private static IEnumerable<string> Programs(string dir, int depth, CancellationToken cancel) =>
        Walk.Files(dir, depth, cancel).Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !Noise().IsMatch(Path.GetFileNameWithoutExtension(f)));

    private void AddApp(string name, string exe)
    {
        RepoInfo? info = ResolveOrigin(exe);
        string ver = o.Probe.Probe(exe).ProductVersion;
        rows.Add(new ScanRow(ScanKind.App, name, info?.Site ?? "", info?.Repo ?? "", false, exe, ver.Length > 0 ? $"version {ver}" : "", info?.Url ?? ""));
    }

    // 4. Mods, texture packs, cheats and patches inside emulators. Returns how many came from a code host and aren't tracked.
    private int ScanMods(CancellationToken cancel)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (string root in o.ModRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string box in Walk.AllDirs(root, 7, cancel).Where(IsModBox))
            {
                var expanded = new List<string>();
                foreach (string it in Walk.Dirs(box))
                {
                    string n = Path.GetFileName(it);
                    if (Containers().IsMatch(n))
                    {
                        // Dolphin-style Load\Textures\<game>: the game folders are the mods
                        expanded.AddRange(Walk.Dirs(it));
                    }
                    else if (TitleId().IsMatch(n))
                    {
                        expanded.AddRange(Walk.Dirs(it));
                    }
                    else if (n.Equals("contents", StringComparison.OrdinalIgnoreCase))
                    {
                        expanded.AddRange(Walk.Dirs(it).SelectMany(Walk.Dirs));
                    }
                    else
                    {
                        expanded.Add(it);
                    }
                }

                expanded.AddRange(Walk.Files(box, 0, cancel).Where(f => ModFileTypes().IsMatch(f)));
                foreach (string m in expanded)
                {
                    string name = Path.GetFileName(m);
                    if (seen.Contains(m))
                    {
                        continue;
                    }

                    // the emulated SD card itself
                    if (Path.GetFileName(box).Equals("sdmc", StringComparison.OrdinalIgnoreCase) && SdCardFolders().IsMatch(name))
                    {
                        continue;
                    }

                    seen.Add(m);
                    if (name.Equals("downloadedGraphicPacks", StringComparison.OrdinalIgnoreCase))
                    {
                        rows.Add(new ScanRow(ScanKind.Mod, "Cemu community graphic packs", "Cemu updater", "", true, m, "updated by Cemu itself"));
                        continue;
                    }

                    List<string> files = Directory.Exists(m) ? [.. Walk.Files(m, int.MaxValue, cancel)] : [m];
                    if (files.Count == 0)
                    {
                        continue;
                    }

                    double size = Math.Round(files.Sum(f => new FileInfo(f).Length) / 1048576.0, 1);
                    string src = "";
                    string repo = "";
                    string from = "";
                    foreach (string f in files.Take(25))
                    {
                        if (Origin(f) is not (string host, string referrer))
                        {
                            continue;
                        }

                        RepoInfo? info = SourceUrls.Parse(referrer, o.RepoIds) ?? SourceUrls.Parse(host, o.RepoIds);
                        if (info is null && referrer.Length > 0)
                        {
                            archiveOrigin.TryGetValue(referrer.ToLowerInvariant(), out info);
                        }

                        if (info is not null)
                        {
                            src = info.Site;
                            repo = info.Repo;
                            break;
                        }

                        if (LocalPath().IsMatch(referrer))
                        {
                            from = $"unpacked from {referrer.Split('\\', '/')[^1]}";
                        }
                        else if (Uri.TryCreate(host, UriKind.Absolute, out Uri? hu) && (hu.Scheme == "http" || hu.Scheme == "https"))
                        {
                            src = hu.Host;
                            break;
                        }
                    }

                    string game = Path.GetFileName(Parent(m));
                    string label = TitleId().IsMatch(game) ? $"{name}  [{game}]" : name;
                    bool tracked = IsTrackedRepo(repo);
                    rows.Add(new ScanRow(ScanKind.Mod, label, src, repo, tracked, m, $"{Path.GetFileName(box)}, {size.ToString(System.Globalization.CultureInfo.InvariantCulture)} MB {from}".Trim()));
                    if (!tracked && repo.Length > 0)
                    {
                        count++;
                    }
                }
            }
        }

        return count;
    }

    private static bool IsModBox(string dir)
    {
        string full = dir.Replace('\\', '/');
        return Containers().IsMatch(Path.GetFileName(dir))
            && !ModSkip().IsMatch(full)
            && !Containers().IsMatch(Path.GetFileName(Parent(dir)));
    }

    // The Scan window's checklist (Get-ScanCandidates)
    private (List<ScanCandidate> List, int Hidden) Candidates()
    {
        var list = new List<ScanCandidate>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var ignore = new HashSet<string>(o.ScanIgnore.Select(s => s.ToLowerInvariant()), StringComparer.Ordinal);
        var exeDirs = new List<string>();
        int hidden = 0;
        void Add(CandidateKind kind, string name, string from, string exe, JsonObject def, bool selected, string note, string key)
        {
            if (key.Length > 0 && !keys.Add(key))
            {
                return;
            }

            if (key.Length > 0 && ignore.Contains(key))
            {
                hidden++;
                return;
            }

            if (exe.Length > 0)
            {
                exeDirs.Add(Key(Parent(exe)));
            }

            list.Add(new ScanCandidate(kind, name, from, exe, def, selected, note, key));
        }

        // 1. recognized apps: complete entries, ticked
        foreach (ScanRow r in rows.Where(r => r.Kind == ScanKind.Known && !r.Tracked))
        {
            Add(CandidateKind.Known, r.Name, r.Source, r.Path, r.Def!, true, "Recognized at its usual install path", (r.Repo.Length > 0 ? r.Repo : r.Name).ToLowerInvariant());
        }

        // 2. programs whose files came from a code host or itch.io page, ticked
        foreach (ScanRow r in rows.Where(r => r.Kind == ScanKind.App && (r.Repo.Length > 0 || r.Url.Length > 0)))
        {
            string dir = Key(Parent(r.Path));
            if (exeDirs.Exists(d => dir == d || dir.StartsWith(d + "\\", StringComparison.Ordinal) || d.StartsWith(dir + "\\", StringComparison.Ordinal)))
            {
                continue;
            }

            if (IsTrackedRepo(r.Repo) || IsTrackedUrl(r.Url))
            {
                continue;
            }

            var info = new RepoInfo(r.Source, r.Repo, r.Url);
            string from = r.Repo.Length > 0 ? $"{r.Repo} ({r.Source})" : $"{r.Url} (itch.io)";
            Add(CandidateKind.Found, r.Name, from, r.Path, NewAppDef(r.Name, info, r.Path), true, "Its files came from this page", (r.Repo.Length > 0 ? r.Repo : r.Url).ToLowerInvariant());
        }

        // 3. downloads from a code host with no installed program found: needs Locate.
        //    Files for phones, Linux or macOS (an .apk synced from a handheld, say) are left out.
        foreach (ScanRow r in rows.Where(r => r.Kind == ScanKind.Download && !r.Tracked && (r.Repo.Length > 0 || r.Url.Length > 0) && !NotWindows().IsMatch(r.Name)))
        {
            string key = (r.Repo.Length > 0 ? r.Repo : r.Url).ToLowerInvariant();
            string shortName = r.Repo.Length > 0 ? r.Repo.Split('/')[^1] : r.Url.TrimEnd('/').Split('/')[^1];
            var info = new RepoInfo(r.Source, r.Repo, r.Url);
            string from = r.Repo.Length > 0 ? $"{r.Repo} ({r.Source})" : $"{r.Url} (itch.io)";
            Add(CandidateKind.Download, shortName, from, "", NewAppDef(shortName, info, ""), false, $"Downloaded {r.Name}; click Locate to pick the installed .exe", key);
        }

        return (list, hidden);
    }

    /// <summary>For tests of the checklist rules: builds candidates from given rows.</summary>
    internal (List<ScanCandidate> List, int Hidden) CandidatesFrom(IEnumerable<ScanRow> given)
    {
        rows.Clear();
        rows.AddRange(given);
        return Candidates();
    }

    [GeneratedRegex(@"/repos/([^/]+/[^/]+)/releases", RegexOptions.IgnoreCase)]
    private static partial Regex ReposApi();

    [GeneratedRegex(@"/projects/([^/]+)/releases", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectsApi();

    [GeneratedRegex(@"\.(zip|7z|rar|exe|msi|apk|appimage|tar|gz|xz|jar|nsp|bat)$", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadTypes();

    /// <summary>Downloads that can't be a Windows program.</summary>
    [GeneratedRegex(@"\.(apk|aab|ipa|appimage|dmg|pkg|deb|rpm|flatpak|snap|nsp|xci|nro)$|[-_.](android|linux|macos|mac|osx|darwin|ios)([-_.]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex NotWindows();

    [GeneratedRegex(@"unins|uninstall|crash|report|updater?$|update$|vc_?redist|dxsetup|QtWebEngine|setup|installer|helper|elevate|createdump|\.backup-", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex("^(load|mods|graphicPacks|GraphicMods|textures|cheats|patches|ResourcePacks|sdmc)$", RegexOptions.IgnoreCase)]
    private static partial Regex Containers();

    /// <summary>Not your mods: shader packs, backups, EmuDeck's configs, game libraries, and what emulators ship (Dolphin's Sys, resources, shaders).</summary>
    [GeneratedRegex("/(reshade-shaders|\\.backup-[^/]+|backend/configs|Mods & Texture Packs)/|/(roms|saves)/|/(Sys|resources|shaders(_[a-z]+)?)/", RegexOptions.IgnoreCase)]
    private static partial Regex ModSkip();

    [GeneratedRegex("^[0-9a-f]{16}$", RegexOptions.IgnoreCase)]
    private static partial Regex TitleId();

    [GeneratedRegex(@"\.(pnach|cht|ips|bps|ups|patch|yml|yaml|zip)$", RegexOptions.IgnoreCase)]
    private static partial Regex ModFileTypes();

    [GeneratedRegex("^(Nintendo 3DS|Nintendo|atmosphere|switch)$", RegexOptions.IgnoreCase)]
    private static partial Regex SdCardFolders();

    [GeneratedRegex(@"^[A-Za-z]:\\")]
    private static partial Regex LocalPath();
}
