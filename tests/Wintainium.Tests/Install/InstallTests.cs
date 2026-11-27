using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Wintainium.Core.Data;
using Wintainium.Core.Install;
using Wintainium.Core.Logging;
using Wintainium.Core.Releases;
using Wintainium.Core.Status;
using Wintainium.Tests.Helpers;

namespace Wintainium.Tests.Install;

/// <summary>Unpacking, zip updates with backups, mods, the EmuDeck bridge and the update methods.</summary>
public sealed class InstallTests : IDisposable
{
    private const string ZipUrl = "https://dl.example/rel/Mod-v1.3.zip";
    private readonly InstallRig rig;
    private byte[] zipBytes = [];

    public InstallTests()
    {
        rig = new InstallRig(new FakeHttp().On(ZipUrl, _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) }));
    }

    public void Dispose() => rig.Dispose();

    private static byte[] MakeZip(params (string Name, string Text)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string text) in entries)
            {
                ZipArchiveEntry e = zip.CreateEntry(name);
                if (!name.EndsWith('/'))
                {
                    using Stream s = e.Open();
                    s.Write(Encoding.UTF8.GetBytes(text));
                }
            }
        }

        return ms.ToArray();
    }

    private string ZipFileOf(params (string Name, string Text)[] entries)
    {
        string f = rig.Dir.File("dl/" + Guid.NewGuid().ToString("N") + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllBytes(f, MakeZip(entries));
        return f;
    }

    private static AppEntry Entry(string json) => new(JsonNode.Parse(json)!.AsObject());

    private static string Esc(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    [Fact(DisplayName = "unzip flattens the single top folder")]
    public async Task Unzip_flattens_single_top_folder()
    {
        string dest = rig.Dir.File("apps/Tool");
        await Archives.ExpandToFolderAsync(ZipFileOf(("Tool-1.2/Tool.exe", "x"), ("Tool-1.2/data/a.txt", "a")), dest, rig.Ctx);
        Assert.True(File.Exists(Path.Combine(dest, "Tool.exe")));
        Assert.True(File.Exists(Path.Combine(dest, "data", "a.txt")));
    }

    [Theory(DisplayName = "X-01: archive entries that would land outside the folder are refused")]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    public async Task Zip_slip_refused(string entry)
    {
        string dest = rig.Dir.File("apps/Tool");
        await Assert.ThrowsAsync<UnsafeArchiveException>(() => Archives.ExpandToFolderAsync(ZipFileOf(("ok.txt", "x"), (entry, "evil")), dest, rig.Ctx));
        Assert.False(File.Exists(rig.Dir.File("apps/evil.txt")));
        Assert.False(File.Exists(rig.Dir.File("evil.txt")));
        Assert.Empty(Directory.GetDirectories(rig.Ctx.TempDir));
    }

    [Fact(DisplayName = "X-02: unpacked programs and scripts keep the download mark")]
    public async Task Unpacked_programs_keep_the_mark()
    {
        string zip = ZipFileOf(("Tool.exe", "x"), ("run.bat", "x"), ("readme.txt", "x"));
        rig.Mark.Mark(zip, ZipUrl);
        string dest = rig.Dir.File("apps/Tool");
        await Archives.ExpandToFolderAsync(zip, dest, rig.Ctx);
        Assert.Contains("HostUrl=" + ZipUrl, rig.Mark.Read(Path.Combine(dest, "Tool.exe")), StringComparison.Ordinal);
        Assert.NotNull(rig.Mark.Read(Path.Combine(dest, "run.bat")));
        Assert.Null(rig.Mark.Read(Path.Combine(dest, "readme.txt")));
    }

    [Fact(DisplayName = "X-08: a program kept in a subfolder of the archive updates the right folder")]
    public async Task Exe_in_subfolder_updates_the_right_folder()
    {
        string root = rig.Dir.File("apps/Emu");
        string exe = rig.Dir.Write("apps/Emu/bin/emu.exe", "old");
        AppEntry app = Entry($$"""{ "name": "Emu", "exe": "{{Esc(exe)}}" }""");
        rig.Prompts.Confirms.Enqueue(true);
        Assert.True(await ZipInstaller.InstallAsync(app, ZipFileOf(("Emu-2.0/bin/emu.exe", "new"), ("Emu-2.0/readme.txt", "r")), rig.Ctx));
        Assert.Equal("new", File.ReadAllText(exe));
        Assert.True(File.Exists(Path.Combine(root, "readme.txt")));
        Assert.False(Directory.Exists(Path.Combine(root, "bin", "bin")));
    }

    [Fact]
    public void Cannot_unpack_other_types_or_7z_without_EmuDeck()
    {
        Assert.True(Archives.CanUnpack("a.ZIP", rig.Ctx.SevenZip));
        Assert.False(Archives.CanUnpack("a.7z", rig.Ctx.SevenZip));
        Assert.False(Archives.CanUnpack("a.exe", rig.Ctx.SevenZip));
        rig.Dir.Write("AppData/EmuDeck/backend/wintools/7z.exe", "x");
        Assert.True(Archives.CanUnpack("a.7z", rig.Ctx.SevenZip));
    }

    [Fact]
    public async Task Seven_zip_is_used_when_EmuDeck_has_it()
    {
        rig.Dir.Write("AppData/EmuDeck/backend/wintools/7z.exe", "x");
        rig.Runner = new FakeRunner((_, args) =>
        {
            string outDir = args.Single(a => a.StartsWith("-o", StringComparison.Ordinal))[2..];
            File.WriteAllText(Path.Combine(outDir, "Tool.exe"), "from 7z");
            return new ProcessOutcome(0, "");
        });
        string dest = rig.Dir.File("apps/Tool");
        await Archives.ExpandToFolderAsync(ZipFileOf(("Tool.exe", "zip")), dest, rig.Rebuild());
        Assert.Equal("from 7z", File.ReadAllText(Path.Combine(dest, "Tool.exe")));
        Assert.Equal(rig.Ctx.SevenZip, rig.Runner.Runs.Single().File);
        Assert.Equal(["x", "-aoa", "-snl-", "--"], rig.Runner.Runs.Single().Args.Where(a => !a.StartsWith("-o", StringComparison.Ordinal) && !a.EndsWith(".zip", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Other_file_types_are_saved_and_shown()
    {
        string exe = rig.Dir.Write("apps/Tool/Tool.exe", "x");
        string file = rig.Dir.Write("dl/Tool.msi", "x");
        Assert.False(await ZipInstaller.InstallAsync(Entry($$"""{ "name": "Tool", "exe": "{{Esc(exe)}}" }"""), file, rig.Ctx));
        Assert.Contains("I cannot unpack this file type here", rig.Log.AllText, StringComparison.Ordinal);
        Assert.Equal([file], rig.Shell.Shown);
    }

    [Fact]
    public async Task Zip_update_refused_while_the_program_runs()
    {
        string exe = rig.Dir.Write("apps/Tool/Tool.exe", "x");
        rig.Shell.Running.Add("Tool");
        Assert.False(await ZipInstaller.InstallAsync(Entry($$"""{ "name": "Tool", "exe": "{{Esc(exe)}}" }"""), ZipFileOf(("Tool.exe", "n")), rig.Ctx));
        Assert.Contains("Tool is running. Close it and try again.", rig.Log.AllText, StringComparison.Ordinal);
        Assert.Equal("x", File.ReadAllText(exe));
    }

    [Fact]
    public async Task Zip_update_cancelled_changes_nothing()
    {
        string exe = rig.Dir.Write("apps/Tool/Tool.exe", "x");
        rig.Prompts.Confirms.Enqueue(false);
        Assert.False(await ZipInstaller.InstallAsync(Entry($$"""{ "name": "Tool", "exe": "{{Esc(exe)}}" }"""), ZipFileOf(("Tool.exe", "n")), rig.Ctx));
        Assert.Contains("  Cancelled.", rig.Log.AllText, StringComparison.Ordinal);
        Assert.Equal("x", File.ReadAllText(exe));
        Assert.Single(Directory.GetDirectories(rig.Dir.File("apps")));
    }

    [Fact]
    public async Task Zip_update_backs_up_says_how_to_roll_back_and_shows_the_hint()
    {
        string exe = rig.Dir.Write("apps/Tool/Tool.exe", "old");
        rig.Prompts.Confirms.Enqueue(true);
        Assert.True(await ZipInstaller.InstallAsync(Entry($$"""{ "name": "Tool", "exe": "{{Esc(exe)}}", "hint": "Run it once." }"""), ZipFileOf(("Tool.exe", "new")), rig.Ctx));
        string backup = rig.Dir.File("apps/Tool") + ".backup-20261127-1430";
        Assert.Equal("old", File.ReadAllText(Path.Combine(backup, "Tool.exe")));
        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Contains($"                 to {backup}", rig.Log.AllText, StringComparison.Ordinal);
        Assert.Contains("rename the backup back", rig.Log.AllText, StringComparison.Ordinal);
        Assert.Contains("  Then: Run it once.", rig.Log.AllText, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "X-12: old backups are pruned, the newest kept")]
    public void Old_backups_pruned()
    {
        string dir = rig.Dir.File("apps/Tool");
        Directory.CreateDirectory(dir);
        foreach (string s in new[] { "20260101-0900", "20260301-0900", "20261127-1430", "20261127-1430-2" })
        {
            Directory.CreateDirectory(dir + ".backup-" + s);
        }

        Directory.CreateDirectory(dir + ".backup-mine");
        Directory.CreateDirectory(rig.Dir.File("apps/Tool2.backup-20200101-0000"));
        var log = new MemoryLog();
        IReadOnlyList<string> removed = ZipInstaller.PruneBackups(dir, 2, log);
        Assert.Equal([dir + ".backup-20260301-0900", dir + ".backup-20260101-0900"], removed);
        Assert.True(Directory.Exists(dir + ".backup-20261127-1430-2"));
        Assert.True(Directory.Exists(dir + ".backup-20261127-1430"));
        Assert.True(Directory.Exists(dir + ".backup-mine"));
        Assert.True(Directory.Exists(rig.Dir.File("apps/Tool2.backup-20200101-0000")));
    }

    [Fact]
    public void Backup_name_counts_up_within_the_same_minute()
    {
        string dir = rig.Dir.File("apps/Tool");
        Directory.CreateDirectory(dir + ".backup-20261127-1430");
        Assert.Equal(dir + ".backup-20261127-1430-2", ZipInstaller.BackupName(dir, rig.Now));
    }

    [Fact(DisplayName = "mod: Get-AppFolder returns installDir")]
    public void App_folder_is_installDir()
    {
        Assert.Equal("/mods/M", ZipInstaller.AppFolder(Entry("""{ "exe": "/mods/M/patches/p.ips", "installDir": "/mods/M" }""")));
        Assert.Equal(Path.GetDirectoryName("/apps/T/t.exe"), ZipInstaller.AppFolder(Entry("""{ "exe": "/apps/T/t.exe" }""")));
    }

    private (AppList List, AppRow Row, string ModDir) Mod(string installedTag = "v1.2")
    {
        string mod = rig.Dir.File("mods/Mod");
        string ips = rig.Dir.Write("mods/Mod/patches/p.ips", "v1.2");
        string listPath = rig.Dir.Write("wintainium-apps.json", $$"""
            [ { "name": "Mod", "exe": "{{Esc(ips)}}", "installDir": "{{Esc(mod)}}", "source": "github", "repo": "o/mod",
                "asset": "Mod-v[^-]*\\.zip$", "update": ["zip", "page"], "installedTag": "{{installedTag}}" } ]
            """);
        AppList list = AppList.Load(listPath, NullLog.Instance);
        AppEntry entry = list.Find("Mod")!;
        var row = new AppRow
        {
            Entry = entry, Status = Statuses.Update, Installed = installedTag, Latest = "v1.3 (cached)", LatestTag = "v1.3", Released = "",
            Methods = entry.Update, Page = "https://gh/mod", Exe = ips, Assets = [new ReleaseAsset("Mod-v1.3.zip", ZipUrl)],
        };
        zipBytes = MakeZip(("Mod/patches/p.ips", "v1.3"), ("Mod/readme.txt", "r"));
        return (list, row, mod);
    }

    [Fact(DisplayName = "mod update: new files in place, nothing in patches\\, backed up first, installedTag saved")]
    public async Task Mod_update()
    {
        (AppList list, AppRow row, string mod) = Mod();
        rig.Prompts.Confirms.Enqueue(true);
        await new Updater(rig.Ctx, list).InvokeMethodAsync(row, "zip", yes: false);
        Assert.Equal("v1.3", File.ReadAllText(Path.Combine(mod, "patches", "p.ips")));
        Assert.True(File.Exists(Path.Combine(mod, "readme.txt")));
        Assert.False(Directory.Exists(Path.Combine(mod, "patches", "patches")));
        Assert.Equal("v1.2", File.ReadAllText(Path.Combine(mod + ".backup-20261127-1430", "patches", "p.ips")));
        Assert.Contains("\"installedTag\": \"v1.3\"", File.ReadAllText(list.FilePath), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "mod update cancelled: tag unchanged")]
    public async Task Mod_update_cancelled()
    {
        (AppList list, AppRow row, _) = Mod();
        rig.Prompts.Confirms.Enqueue(false);
        await new Updater(rig.Ctx, list).InvokeMethodAsync(row, "zip", yes: false);
        Assert.Contains("\"installedTag\": \"v1.2\"", File.ReadAllText(list.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void EmuDeck_function_names_are_checked()
    {
        Assert.True(EmuDeckBridge.IsInstallFunction("Yuzu_install"));
        Assert.True(EmuDeckBridge.IsInstallFunction("ppsspp_install"));
        Assert.False(EmuDeckBridge.IsInstallFunction("Yuzu_install; Remove-Item C:\\"));
        Assert.False(EmuDeckBridge.IsInstallFunction("yuzu_uninstall2"));
        Assert.False(EmuDeckBridge.IsInstallFunction(""));
    }

    private AppEntry EmuApp(string fn) =>
        Entry($$"""{ "name": "Emu", "exe": "{{Esc(rig.Dir.File("apps/Emu/emu.exe"))}}", "emudeckFn": "{{fn}}", "update": ["emudeck"] }""");

    [Fact]
    public async Task EmuDeck_without_its_backend_opens_EmuDeck_with_instructions()
    {
        await EmuDeckBridge.InstallAsync(EmuApp("emu_install"), rig.Ctx, yes: false);
        Assert.Contains("EmuDeck manages: Emu", rig.Log.AllText, StringComparison.Ordinal);
        Assert.Contains("EmuDeck not found at", rig.Log.AllText, StringComparison.Ordinal);
        rig.Dir.Write("EmuDeck/EmuDeck.exe", "x");
        await EmuDeckBridge.InstallAsync(EmuApp(""), rig.Ctx, yes: false);
        Assert.Equal([rig.Ctx.EmuDeckExe], rig.Shell.Started);
        Assert.Empty(rig.Runner.Runs);
    }

    [Fact(DisplayName = "X-06: a bad emudeckFn is never run")]
    public async Task Bad_EmuDeck_function_not_run()
    {
        rig.Dir.Write("AppData/EmuDeck/backend/functions/all.ps1", "#");
        await EmuDeckBridge.InstallAsync(EmuApp("x; calc"), rig.Ctx, yes: true);
        Assert.Empty(rig.Runner.Runs);
        Assert.Contains("isn't an EmuDeck install function", rig.Log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmuDeck_install_runs_its_function_and_reports_the_new_exe()
    {
        rig.Dir.Write("AppData/EmuDeck/backend/functions/all.ps1", "#");
        string exe = rig.Dir.Write("apps/Emu/emu.exe", "old");
        File.SetLastWriteTime(exe, new DateTime(2026, 1, 1));
        rig.Runner = new FakeRunner((_, _) =>
        {
            File.WriteAllText(exe, "new");
            File.SetLastWriteTime(exe, new DateTime(2026, 11, 27, 14, 0, 0));
            return new ProcessOutcome(0, "");
        });
        rig.Prompts.Confirms.Enqueue(true);
        await EmuDeckBridge.InstallAsync(EmuApp("emu_install"), rig.Rebuild(), yes: false);
        (string file, IReadOnlyList<string> args) = rig.Runner.Runs.Single();
        Assert.EndsWith("powershell.exe", file, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("; & emu_install", args[^1], StringComparison.Ordinal);
        Assert.Contains("emu.exe is now dated 2026-11-27", rig.Log.AllText, StringComparison.Ordinal);
        Assert.Contains("MB in EmuDeck's temp folder", rig.Log.LastProgress, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmuDeck_errors_and_an_unchanged_exe_are_reported()
    {
        rig.Dir.Write("AppData/EmuDeck/backend/functions/all.ps1", "#");
        rig.Dir.Write("apps/Emu/emu.exe", "old");
        rig.Runner = new FakeRunner((_, _) => new ProcessOutcome(1, "download failed\nmore"));
        await EmuDeckBridge.InstallAsync(EmuApp("emu_install"), rig.Rebuild(), yes: true);
        Assert.Contains("  EmuDeck reported: download failed", rig.Log.AllText, StringComparison.Ordinal);
        Assert.Contains("the exe did not change", rig.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Continue?", rig.Prompts.Asked);
    }

    [Fact]
    public async Task EmuDeck_refused_while_the_emulator_runs()
    {
        rig.Dir.Write("AppData/EmuDeck/backend/functions/all.ps1", "#");
        rig.Shell.Running.Add("emu");
        await EmuDeckBridge.InstallAsync(EmuApp("emu_install"), rig.Ctx, yes: true);
        Assert.Empty(rig.Runner.Runs);
        Assert.Contains("Emu is running", rig.Log.AllText, StringComparison.Ordinal);
    }

    private static AppRow PlainRow(AppEntry e, string status = Statuses.Update, string page = "https://gh/x") => new()
    {
        Entry = e, Status = status, Installed = "1", Latest = "2", LatestTag = "2", Released = "", Methods = e.Update, Page = page, Exe = e.ExpandedExe,
    };

    [Fact]
    public async Task Inapp_starts_the_program_or_its_launcher_and_shows_the_hint()
    {
        string exe = rig.Dir.Write("apps/A/a.exe", "x");
        string launcher = rig.Dir.Write("apps/A/launch.bat", "x");
        var u = new Updater(rig.Ctx, null);
        await u.InvokeMethodAsync(PlainRow(Entry($$"""{ "name": "A", "exe": "{{Esc(exe)}}", "hint": "Help > Check for updates" }""")), "inapp", false);
        await u.InvokeMethodAsync(PlainRow(Entry($$"""{ "name": "A", "exe": "{{Esc(exe)}}", "launch": "{{Esc(launcher)}}" }""")), "inapp", false);
        await u.InvokeMethodAsync(PlainRow(Entry("""{ "name": "A", "exe": "/nope/a.exe" }""")), "inapp", false);
        Assert.Equal([exe, launcher], rig.Shell.Started);
        Assert.Contains("  Then: Help > Check for updates", rig.Log.AllText, StringComparison.Ordinal);
        Assert.Contains("  Not found: /nope/a.exe", rig.Log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Installer_download_and_page_methods()
    {
        zipBytes = [1, 2, 3];
        AppEntry e = Entry("""{ "name": "A", "exe": "/x/a.exe" }""");
        AppRow row = PlainRow(e) is var r ? new AppRow
        {
            Entry = e, Status = r.Status, Installed = r.Installed, Latest = r.Latest, LatestTag = r.LatestTag, Released = r.Released,
            Methods = r.Methods, Page = r.Page, Exe = r.Exe, Assets = [new ReleaseAsset("Mod-v1.3.zip", ZipUrl)],
        } : throw new InvalidOperationException();
        var u = new Updater(rig.Ctx, null);
        rig.Prompts.Confirms.Enqueue(true);
        await u.InvokeMethodAsync(row, "installer", false);
        string file = Path.Combine(rig.Ctx.DownloadDir, "Mod-v1.3.zip");
        Assert.Equal([file], rig.Shell.Started);
        Assert.Contains("Run the installer now?", rig.Prompts.Asked);
        await u.InvokeMethodAsync(row, "download", false);
        Assert.Equal([file], rig.Shell.Shown);
        await u.InvokeMethodAsync(row, "page", false);
        await u.InvokeMethodAsync(PlainRow(e, page: ""), "page", false);
        Assert.Equal(["https://gh/x"], rig.Shell.Opened);
        Assert.Contains("No release page is set for this app", rig.Log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_app_asks_which_method_when_there_are_several()
    {
        AppEntry e = Entry("""{ "name": "A", "exe": "/x/a.exe", "update": ["inapp", "page"] }""");
        var u = new Updater(rig.Ctx, null);
        rig.Prompts.Choices.Enqueue(1);
        await u.UpdateAppAsync(PlainRow(e));
        Assert.Equal(["https://gh/x"], rig.Shell.Opened);
        Assert.Contains("How do you want to update A?: in-app | release page", rig.Prompts.Asked);
        rig.Prompts.Choices.Enqueue(null);
        await u.UpdateAppAsync(PlainRow(e));
        Assert.Single(rig.Shell.Opened);
    }

    [Fact]
    public async Task Update_all_asks_each_outdated_app_and_stops_when_asked()
    {
        var u = new Updater(rig.Ctx, null);
        await u.UpdateAllAsync([PlainRow(Entry("""{ "name": "A" }"""), Statuses.UpToDate)]);
        Assert.Contains("Nothing to update.", rig.Log.AllText, StringComparison.Ordinal);

        AppRow[] rows =
        [
            PlainRow(Entry("""{ "name": "A" }""")),
            PlainRow(Entry("""{ "name": "B" }"""), Statuses.UpToDate),
            PlainRow(Entry("""{ "name": "C" }""")),
            PlainRow(Entry("""{ "name": "D" }""")),
        ];
        rig.Prompts.YesNoStops.Enqueue(YesNoStop.Yes);
        rig.Prompts.YesNoStops.Enqueue(YesNoStop.Stop);
        await u.UpdateAllAsync(rows);
        Assert.Equal(["Update A via release page?", "Update C via release page?"], rig.Prompts.Asked);
        Assert.Single(rig.Shell.Opened);
    }
}
