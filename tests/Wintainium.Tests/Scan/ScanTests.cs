using System.Text.Json.Nodes;
using Wintainium.Core.Data;
using Wintainium.Core.Json;
using Wintainium.Core.Scan;
using Wintainium.Core.Status;
using Wintainium.Tests.Helpers;

namespace Wintainium.Tests.Scan;

/// <summary>The checks of Test-SourceDetection.ps1 and Test-Scan.ps1.</summary>
public sealed class ScanTests : IDisposable
{
    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    private sealed class Ids : IRepoIdResolver
    {
        public string Resolve(string id) => id == "1449838890" ? "someone/some-tool" : "";
    }

    private static string RR(string url)
    {
        RepoInfo? r = SourceUrls.Parse(url, new Ids());
        return r is null ? "null" : $"{r.Site}|{r.Repo}";
    }

    [Theory(DisplayName = "Source detection (Test-SourceDetection.ps1)")]
    [InlineData("github release download", "https://github.com/PCSX2/pcsx2/releases/download/v2.9.179/x.7z", "github.com|PCSX2/pcsx2")]
    [InlineData("github releases page referrer", "https://github.com/owner/project/releases", "github.com|owner/project")]
    [InlineData("release-assets host: site only", "https://release-assets.githubusercontent.com/some/other/path?sp=r", "github.com|")]
    [InlineData("raw.githubusercontent repo", "https://raw.githubusercontent.com/o/r/main/f.txt", "github.com|o/r")]
    [InlineData("gitlab package file", "https://gitlab.com/es-de/emulationstation-de/-/package_files/357717909/download", "gitlab.com|es-de/emulationstation-de")]
    [InlineData("forgejo self-hosted", "https://git.example.org/team/tool/releases/download/v1.0/tool.exe", "git.example.org|team/tool")]
    [InlineData("codeberg", "https://codeberg.org/owner/app/releases/download/1.0/a.zip", "codeberg.org|owner/app")]
    [InlineData("unknown host with /releases/download/", "https://example.org/team/tool/releases/download/1/x.zip", "example.org|team/tool")]
    [InlineData("scummvm site is not a code host", "https://downloads.scummvm.org/frs/scummvm/2026.4.0/x.zip", "null")]
    [InlineData("gamebanana is not a code host", "https://gamebanana.com/dl/123456", "null")]
    [InlineData("local path ignored", @"C:\Users\a\Downloads\mod.zip", "null")]
    [InlineData("release-assets repo id resolved", "https://release-assets.githubusercontent.com/github-production-release-asset/1449838890/aef75aea?sp=r", "github.com|someone/some-tool")]
    [InlineData("old objects host repo id resolved", "https://objects.githubusercontent.com/github-production-release-asset-2e65be/1449838890/abc", "github.com|someone/some-tool")]
    public void Source_detection(string check, string url, string want)
    {
        Assert.False(string.IsNullOrEmpty(check));
        Assert.Equal(want, RR(url));
    }

    [Fact(DisplayName = "ssh remote converted")]
    public void Ssh_remote_converted() => Assert.Equal("github.com|Owner/Repo", RR(SourceUrls.FromGitRemote("git@github.com:Owner/Repo.git")));

    [Fact(DisplayName = "itch.io game page recognized; download page keeps the game page; front page is not a game")]
    public void Itch_pages()
    {
        Assert.Equal(new RepoInfo("itch.io", "", "https://cobalttornado1.itch.io/lightbound"), SourceUrls.Parse("https://cobalttornado1.itch.io/lightbound"));
        Assert.Equal("https://dev.itch.io/game", SourceUrls.Parse("https://dev.itch.io/game/download/abc123")!.Url);
        Assert.Null(SourceUrls.Parse("https://www.itch.io/games"));
    }

    [Fact]
    public void Repo_ids_are_looked_up_once_at_api_github_com_with_the_token()
    {
        var http = new FakeHttp().OnJson("https://api.github.com/repositories/42", """{ "full_name": "o/answer" }""");
        using HttpClient client = http.Client();
        var ids = new GitHubRepoIds(client, "ghp_secret");
        Assert.Equal("o/answer", ids.Resolve("42"));
        Assert.Equal("o/answer", ids.Resolve("42"));
        Assert.Equal("", ids.Resolve("7"));
        Assert.Equal("", ids.Resolve("../x"));
        Assert.Equal(2, http.Requests.Count);
        Assert.All(http.Requests, r => Assert.Equal("api.github.com", r.RequestUri!.Host));
        Assert.Equal("Bearer ghp_secret", http.Requests[0].Headers.Authorization!.ToString());
    }

    private static string Update(JsonObject def) => string.Join(',', def.GetStringList("update"));

    [Fact(DisplayName = "New entries: GitHub, itch.io, GitLab and Codeberg")]
    public void New_entries()
    {
        JsonObject d = PcScanner.NewAppDef("Tool", new RepoInfo("github.com", "o/tool"), @"C:\Apps\Tool\tool.exe");
        Assert.Equal(("github", "o/tool", @"C:\Apps\Tool\tool.exe", "download,page"), (d.GetString("source"), d.GetString("repo"), d.GetString("exe"), Update(d)));
        d = PcScanner.NewAppDef("Game", new RepoInfo("itch.io", "", "https://dev.itch.io/game"), @"C:\Games\game.exe");
        Assert.Equal(("itch", "https://dev.itch.io/game", "page"), (d.GetString("source"), d.GetString("url"), Update(d)));
        d = PcScanner.NewAppDef("Lab", new RepoInfo("gitlab.com", "grp/sub/lab"), "x.exe");
        Assert.Equal(("gitlab", "https://gitlab.com/api/v4/projects/grp%2Fsub%2Flab/releases/permalink/latest"), (d.GetString("source"), d.GetString("api")));
        d = PcScanner.NewAppDef("Berg", new RepoInfo("codeberg.org", "o/berg"), "x.exe");
        Assert.Equal(("gitea", "https://codeberg.org/api/v1/repos/o/berg/releases/latest"), (d.GetString("source"), d.GetString("api")));
    }

    private static AppEntry Tracked(string json) => new(JsonNode.Parse(json)!.AsObject());

    [Fact(DisplayName = "Turning scan rows into the checklist")]
    public void Checklist_rules()
    {
        var scanner = new PcScanner(new ScanOptions
        {
            Tracked = [Tracked("""{ "name": "Old", "repo": "o/tracked" }""")],
            ScanIgnore = ["dev/hidden"],
        });
        (List<ScanCandidate> cands, int hidden) = scanner.CandidatesFrom(
        [
            new(ScanKind.Known, "PCSX2", "PCSX2/pcsx2 (GitHub)", "PCSX2/pcsx2", false, @"C:\Emu\PCSX2\pcsx2.exe", "", "", new JsonObject { ["name"] = "PCSX2" }),
            new(ScanKind.App, "PCSX2", "github.com", "PCSX2/pcsx2", false, @"C:\Emu\PCSX2\pcsx2.exe", ""),
            new(ScanKind.App, "CoolTool", "github.com", "dev/cooltool", false, @"C:\Apps\CoolTool\cool.exe", ""),
            new(ScanKind.App, "Game", "itch.io", "", false, @"C:\Games\Game\game.exe", "", "https://dev.itch.io/game"),
            new(ScanKind.App, "Old", "github.com", "o/tracked", false, @"C:\Apps\Old\old.exe", ""),
            new(ScanKind.App, "Random", "", "", false, @"C:\Apps\Random\r.exe", ""),
            new(ScanKind.Download, "cooltool-2.0.zip", "github.com", "dev/cooltool", false, @"C:\Users\u\Downloads\cooltool-2.0.zip", ""),
            new(ScanKind.Download, "other-1.0.zip", "github.com", "dev/other", false, @"C:\Users\u\Downloads\other-1.0.zip", ""),
            new(ScanKind.Download, "banana-native-v0.2.4.apk", "git.example.dev", "someone/BananaNative", false, @"C:\Sync\banana-native-v0.2.4.apk", ""),
            new(ScanKind.Download, "tool-1.0-linux-x64.tar.gz", "github.com", "dev/linuxonly", false, @"C:\Users\u\Downloads\tool-1.0-linux-x64.tar.gz", ""),
            new(ScanKind.Download, "mod-1.0.zip", "github.com", "dev/hidden", false, @"C:\Users\u\Downloads\mod-1.0.zip", ""),
        ]);
        var byName = cands.ToDictionary(c => c.Name);
        Assert.False(byName.ContainsKey("BananaNative")); // Android download (.apk) left out
        Assert.False(byName.ContainsKey("linuxonly")); // Linux-only download left out
        Assert.False(byName.ContainsKey("hidden")); // "Don't show again" item left out and counted
        Assert.Equal(1, hidden);
        Assert.Equal("dev/other", byName["other"].Key); // candidates carry the key used to hide them
        Assert.Single(cands, c => c.Name == "PCSX2"); // recognized app listed once
        Assert.Equal(CandidateKind.Known, byName["PCSX2"].Kind);
        Assert.Equal(CandidateKind.Found, byName["CoolTool"].Kind); // program from GitHub listed and ticked
        Assert.True(byName["CoolTool"].Selected);
        Assert.Equal("dev/cooltool", byName["CoolTool"].Def.GetString("repo"));
        Assert.Equal(("itch", "https://dev.itch.io/game"), (byName["Game"].Def.GetString("source"), byName["Game"].Def.GetString("url"))); // program from an itch.io page listed
        Assert.False(byName.ContainsKey("Old")); // already tracked repo left out
        Assert.False(byName.ContainsKey("Random")); // program of unknown origin left out
        Assert.Single(cands, c => c.From.StartsWith("dev/cooltool", StringComparison.Ordinal)); // download for an installed program not repeated
        Assert.Equal(CandidateKind.Download, byName["other"].Kind); // download with no program found needs Locate, unticked
        Assert.False(byName["other"].Selected);
        Assert.Equal("", byName["other"].Exe);
    }

    /// <summary>A fake profile like Test-Scan.ps1's: EmuDeck emulators, Playnite, a tools folder.</summary>
    private sealed class Profile
    {
        public Profile(TempDir dir)
        {
            Dir = dir;
            New("Roaming/EmuDeck/Emulators/PCSX2-Qt/pcsx2-qtx64.exe");
            New("Roaming/EmuDeck/Emulators/xemu/xemu.exe");
            New("Local/Playnite/Playnite.dll");
            New("Emu/tools/srm.exe");
            dir.Write("Roaming/EmuDeck/settings.ps1", $"$emulationPath=\"{dir.File("Emu")}\"\n");
            Directory.CreateDirectory(dir.File("Self"));
        }

        public TempDir Dir { get; }

        public FakeMark Marks { get; } = new();

        public List<AppEntry> Tracked { get; } = [Tracked("""{ "name": "xemu", "exe": "%APPDATA%\\EmuDeck\\Emulators\\xemu\\xemu.exe", "source": "github", "repo": "xemu-project/xemu", "update": ["page"] }""")];

        public List<string> Ignore { get; } = [];

        public string New(string rel) => Dir.Write(rel, "x");

        public string Expand(string s) => s
            .Replace("%APPDATA%", Dir.File("Roaming"), StringComparison.OrdinalIgnoreCase)
            .Replace("%LOCALAPPDATA%", Dir.File("Local"), StringComparison.OrdinalIgnoreCase)
            .Replace("%USERPROFILE%", Dir.File("User"), StringComparison.OrdinalIgnoreCase);

        public ScanResult Run()
        {
            string emu = ScanOptions.ReadEmulationPath([Dir.File("Roaming/EmuDeck/settings.ps1")]) ?? ScanOptions.DefaultEmulationPath;
            var opts = new ScanOptions
            {
                DownloadRoots = [Dir.File("User/Downloads"), emu],
                AppRoots = [Dir.File("User/Wintainium/Apps"), Path.Combine(emu, "tools"), Dir.File("Roaming/EmuDeck/Emulators")],
                ModRoots = [emu],
                DownloadsFolder = Dir.File("User/Downloads"),
                EmulationPath = emu,
                Catalog = Catalog.LoadBuiltIn(),
                Tracked = Tracked,
                ScanIgnore = Ignore,
                SelfFolder = Dir.File("Self"),
                EmuDeckBackend = File.Exists(Dir.File("Roaming/EmuDeck/backend/functions/all.ps1")),
                Expand = Expand,
                Marks = Marks,
                Probe = new FileExeProbe(),
            };
            return new PcScanner(opts).Run();
        }
    }

    private static ScanCandidate? Named(ScanResult r, string name) => r.Candidates.FirstOrDefault(c => c.Name == name);

    [Fact(DisplayName = "Full scan in a fake profile: recognized apps, EmuDeck folder, tracked apps, portable paths, EmuDeck method")]
    public void Full_scan()
    {
        var p = new Profile(dir);
        ScanResult res = p.Run();
        Assert.NotNull(Named(res, "PCSX2")); // recognized apps found (PCSX2, Playnite)
        Assert.NotNull(Named(res, "Playnite"));
        Assert.NotNull(Named(res, "Steam ROM Manager")); // EmuDeck folder read from its settings
        Assert.Null(Named(res, "xemu")); // tracked app (xemu) left out
        ScanCandidate pcsx2 = Named(res, "PCSX2")!;
        Assert.True(pcsx2.Selected); // recognized apps are ticked
        Assert.Equal(CandidateKind.Known, pcsx2.Kind);
        Assert.Equal(@"%APPDATA%\EmuDeck\Emulators\PCSX2-Qt\pcsx2-qtx64.exe", pcsx2.Def.GetString("exe")); // entry keeps the portable %APPDATA% path
        Assert.Equal("inapp,page", Update(pcsx2.Def)); // without EmuDeck installed, its update method is dropped
        Assert.Null(pcsx2.Def.GetField("emudeckFn"));
        ScanCandidate playnite = Named(res, "Playnite")!;
        Assert.True(playnite.Def.IsTruthy("launch") && playnite.Def.IsTruthy("hint")); // catalog extras kept
        Assert.Equal("JosefNemec/Playnite", playnite.Def.GetString("repo"));

        p.New("Roaming/EmuDeck/backend/functions/all.ps1");
        pcsx2 = Named(p.Run(), "PCSX2")!;
        Assert.Equal("emudeck,inapp,page", Update(pcsx2.Def)); // with EmuDeck installed, updates go through EmuDeck
        Assert.Equal("PCSX2QT_install", pcsx2.Def.GetString("emudeckFn"));
    }

    [Fact(DisplayName = "Wintainium offers itself: page for a portable copy, the verified Wintainium.exe when installed, not when tracked")]
    public void Self_entry()
    {
        var p = new Profile(dir);
        Assert.Null(Named(p.Run(), "Wintainium"));
        string exe = p.New("Self/Wintainium.exe");
        ScanCandidate w = Named(p.Run(), "Wintainium")!;
        Assert.True(w.Selected);
        Assert.Equal(CandidateKind.Known, w.Kind);
        Assert.Equal("Ay1man2/Wintainium", w.Def.GetString("repo"));
        Assert.Equal(exe, w.Def.GetString("exe")); // Wintainium entry prefers Wintainium.exe
        Assert.Equal("page", Update(w.Def)); // zip or clone copy updates from the release page
        p.New("Self/" + DataFolder.InstalledVersionFileName);
        w = Named(p.Run(), "Wintainium")!;
        Assert.Equal("installer,page", Update(w.Def)); // installed copy updates itself from the verified Wintainium.exe
        Assert.Matches(w.Def.GetString("asset"), "Wintainium.exe");
        Assert.DoesNotMatch(w.Def.GetString("asset"), "Wintainium-0.2.0-portable.zip");
        p.Tracked.Add(Tracked($$"""{ "name": "Wintainium", "exe": "{{exe.Replace("\\", "\\\\", StringComparison.Ordinal)}}", "repo": "Ay1man2/Wintainium" }"""));
        Assert.Null(Named(p.Run(), "Wintainium")); // tracked Wintainium not offered again
    }

    [Fact(DisplayName = "hidden recognized app left out")]
    public void Hidden_recognized_app()
    {
        var p = new Profile(dir);
        p.Ignore.Add("pcsx2/pcsx2");
        ScanResult res = p.Run();
        Assert.Null(Named(res, "PCSX2"));
        Assert.Equal(1, res.Hidden);
    }

    [Fact]
    public void Programs_and_downloads_with_a_download_mark_are_found()
    {
        var p = new Profile(dir);
        string tool = p.New("User/Wintainium/Apps/CoolTool/cool.exe");
        p.New("User/Wintainium/Apps/CoolTool/unins000.exe");
        p.Marks.Mark(tool, "https://github.com/dev/cooltool/releases/download/v1/cool.zip");
        string other = p.New("User/Downloads/other-1.0.zip");
        p.Marks.Marks[Path.GetFullPath(other)] = "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://github.com/dev/other/releases\r\nHostUrl=https://objects.githubusercontent.com/x\r\n";
        string game = p.New("User/Wintainium/Apps/Game/game.exe");
        p.Marks.Mark(game, "https://dev.itch.io/game/download/abc");
        p.New("User/Downloads/saves/x.zip");
        ScanResult res = p.Run();
        Assert.Equal(CandidateKind.Found, Named(res, "CoolTool")!.Kind);
        Assert.Equal(tool, Named(res, "CoolTool")!.Exe);
        Assert.Equal(CandidateKind.Download, Named(res, "other")!.Kind);
        Assert.Equal("https://dev.itch.io/game", Named(res, "Game")!.Def.GetString("url"));
        Assert.DoesNotContain(res.Rows, r => r.Path.Contains("saves", StringComparison.Ordinal));
    }

    [Fact]
    public void A_program_unpacked_from_a_marked_archive_gets_the_archive_origin()
    {
        var p = new Profile(dir);
        string zip = p.New("User/Downloads/emu-2.0.zip");
        p.Marks.Mark(zip, "https://github.com/dev/emu/releases/download/v2/emu-2.0.zip");
        string exe = p.New("User/Wintainium/Apps/Emu/emu.exe");
        p.Marks.Marks[Path.GetFullPath(exe)] = $"[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl={zip}\r\n";
        Assert.Equal("dev/emu", Named(p.Run(), "Emu")!.Def.GetString("repo"));
    }

    [Fact]
    public void Mods_from_a_code_host_are_counted()
    {
        var p = new Profile(dir);
        string mod = p.New("Emu/storage/load/0100000000010000/CoolMod/romfs/a.bin");
        p.Marks.Mark(mod, "https://github.com/modder/coolmod/releases/download/v1/m.zip");
        p.New("Emu/storage/load/0100000000010000/LocalMod/romfs/a.bin");
        string shipped = p.New("Emu/Dolphin/Sys/Load/Textures/GAME/t.png");
        p.Marks.Mark(shipped, "https://github.com/dolphin/dolphin/releases/download/v1/d.zip");
        ScanResult res = p.Run();
        Assert.Equal(1, res.ModCount);
        Assert.Contains(res.Rows, r => r.Kind == ScanKind.Mod && r.Name == "CoolMod  [0100000000010000]" && r.Repo == "modder/coolmod");
        Assert.DoesNotContain(res.Rows, r => r.Path.Contains("Sys", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Scan only reads: no file changes")]
    public void Scan_only_reads()
    {
        var p = new Profile(dir);
        p.New("Self/Wintainium.exe");
        string before = Snapshot(dir.Path);
        p.Run();
        Assert.Equal(before, Snapshot(dir.Path));
    }

    private static string Snapshot(string root) => string.Join('\n', Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(f => f + "|" + (File.Exists(f) ? File.GetLastWriteTimeUtc(f).Ticks : 0)));

    [Fact]
    public void Built_in_catalog_has_no_Switch_emulators()
    {
        IReadOnlyList<JsonObject> cat = Catalog.LoadBuiltIn();
        Assert.True(cat.Count > 10);
        string all = string.Join('\n', cat.Select(c => c.ToJsonString())).ToLowerInvariant();
        foreach (string banned in new[] { "yuzu", "ryujinx", "ryubing", "eden", "suyu", "sudachi", "citron", "switch" })
        {
            Assert.DoesNotContain(banned, all, StringComparison.Ordinal);
        }
    }
}
