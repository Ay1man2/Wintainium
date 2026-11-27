using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Wintainium.Core.Data;
using Wintainium.Core.Install;
using Wintainium.Core.Logging;
using Wintainium.Core.Releases;
using Wintainium.Core.Session;
using Wintainium.Tests.Helpers;

namespace Wintainium.Tests.Session;

/// <summary>Add app (A-01 to A-09), with the network, prompts and folders faked.</summary>
public sealed class AddAppTests : IDisposable
{
    private const string Base = "https://dl.example/rel/";
    private readonly InstallRig rig;
    private readonly AppList list;
    private ReleaseInfo? latest;
    private int lookups;

    public AddAppTests()
    {
        var http = new FakeHttp()
            .On(Base + "Tool-1.4.2-windows-x64.zip", _ => Bytes(Zip(("Tool/tool.exe", "exe"), ("Tool/readme.txt", "hi"))))
            .On(Base + "Pack-v3.zip", _ => Bytes(Zip(("patches/a.ips", "x"), ("readme.txt", "hi"))))
            .On(Base + "Setup-1.4.2.exe", _ => Bytes(Encoding.UTF8.GetBytes("installer")))
            .On(Base + "Two-1.0.zip", _ => Bytes(Zip(("a.exe", "a"), ("sub/b.exe", "b"))));
        rig = new InstallRig(http);
        rig.Dir.Write("apps.json", "[]");
        list = AppList.Load(rig.Dir.File("apps.json"), rig.Log);
    }

    public void Dispose() => rig.Dispose();

    private static HttpResponseMessage Bytes(byte[] b) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string text) in entries)
            {
                using Stream s = zip.CreateEntry(name).Open();
                s.Write(Encoding.UTF8.GetBytes(text));
            }
        }

        return ms.ToArray();
    }

    private static ReleaseInfo Release(string tag, params string[] files) => new()
    {
        Tag = tag,
        Date = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc),
        Page = "https://github.com/o/tool/releases/tag/" + tag,
        Assets = [.. files.Select(f => new ReleaseAsset(f, Base + f))],
        Notes = "",
    };

    private AddApp Flow()
    {
        var updater = new Updater(rig.Ctx, list);
        return new AddApp(rig.Ctx, updater, list, rig.Dir.File("Apps"), (_, _) =>
        {
            lookups++;
            return Task.FromResult(latest);
        });
    }

    private void Answers(params string[] answers)
    {
        foreach (string a in answers)
        {
            rig.Prompts.Answers.Enqueue(a);
        }
    }

    private AppEntry Saved() => Assert.Single(AppList.Load(list.FilePath, rig.Log).Entries);

    // ---- A-01: links ----

    [Theory]
    [InlineData("https://github.com/PCSX2/pcsx2", "github", "PCSX2/pcsx2", "", "", "pcsx2")]
    [InlineData("https://github.com/PCSX2/pcsx2.git", "github", "PCSX2/pcsx2", "", "", "pcsx2")]
    [InlineData("https://github.com/PCSX2/pcsx2/releases/tag/v2.0", "github", "PCSX2/pcsx2", "", "", "pcsx2")]
    [InlineData("  PCSX2/pcsx2  ", "github", "PCSX2/pcsx2", "", "", "pcsx2")]
    [InlineData("https://maker.itch.io/cool-game", "itch", "", "https://maker.itch.io/cool-game", "", "cool-game")]
    [InlineData("http://maker.itch.io/cool-game/devlog", "itch", "", "https://maker.itch.io/cool-game", "", "cool-game")]
    [InlineData("https://gitlab.com/group/sub/proj", "gitlab", "", "", "https://gitlab.com/api/v4/projects/group%2Fsub%2Fproj/releases/permalink/latest", "proj")]
    [InlineData("https://gitlab.com/group/proj/-/releases", "gitlab", "", "", "https://gitlab.com/api/v4/projects/group%2Fproj/releases/permalink/latest", "proj")]
    [InlineData("https://codeberg.org/owner/app", "gitea", "", "", "https://codeberg.org/api/v1/repos/owner/app/releases/latest", "app")]
    [InlineData("https://git.example.org/owner/app.git", "gitea", "", "", "https://git.example.org/api/v1/repos/owner/app/releases/latest", "app")]
    public void Links_are_recognised(string text, string source, string repo, string url, string api, string shortName) =>
        Assert.Equal(new AddSource(source, repo, url, api, shortName), AddApp.ParseSource(text));

    [Theory]
    [InlineData("")]
    [InlineData("just words")]
    [InlineData("https://example.org")]
    [InlineData("ftp://example.org/a/b")]
    public void Other_text_is_not_a_link(string text) => Assert.Null(AddApp.ParseSource(text));

    [Fact]
    public void Passwords_in_a_link_are_dropped()
    {
        AddSource? s = AddApp.ParseSource("https://user:secret@git.example.org/owner/app");
        Assert.Equal("https://git.example.org/api/v1/repos/owner/app/releases/latest", s!.Api);
    }

    [Theory]
    [InlineData("Tool-1.4.2-windows-x64.zip", "v1.4.2", @"^Tool-.+-windows-x64\.zip$")]
    [InlineData("Pack.zip", "v3", @"^Pack\.zip$")]
    [InlineData("Pack.zip", "nightly", @"^Pack\.zip$")]
    public void Asset_pattern_wildcards_the_version(string file, string tag, string pattern) =>
        Assert.Equal(pattern, AddApp.AssetPattern(file, tag));

    // ---- flows ----

    [Fact]
    public async Task Not_a_link_is_cancelled()
    {
        Answers("hello");
        Assert.Null(await Flow().RunAsync());
        Assert.Contains("  Cancelled: that does not look like a repo or release URL.", rig.Log.AllText, StringComparison.Ordinal);
        Assert.Empty(list.Entries);
    }

    [Fact]
    public async Task Itch_tracks_an_installed_copy()
    {
        Answers("https://maker.itch.io/cool-game", "", "\"C:\\Games\\Cool\\cool.exe\"");
        AppEntry? e = await Flow().RunAsync();
        Assert.NotNull(e);
        AppEntry saved = Saved();
        Assert.Equal("cool-game", saved.Name);
        Assert.Equal("itch", saved.Source);
        Assert.Equal(@"C:\Games\Cool\cool.exe", saved.Exe);
        Assert.Equal(["page"], saved.Update);
        Assert.Equal(["name", "source", "url", "exe", "update"], saved.Json.Select(kv => kv.Key));
        Assert.Contains("Updates open its itch.io page.", rig.Log.AllText, StringComparison.Ordinal);
        Assert.Equal(0, lookups);
    }

    [Fact]
    public async Task Itch_without_an_exe_is_cancelled()
    {
        Answers("https://maker.itch.io/cool-game", "Cool", "");
        Assert.Null(await Flow().RunAsync());
        Assert.Contains("an itch.io app needs the path to its .exe", rig.Log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_installed_exe_is_tracked_by_download()
    {
        Answers("o/tool", "My Tool", @"C:\Tools\tool.exe");
        rig.Prompts.Confirms.Enqueue(true); // pre-releases
        await Flow().RunAsync();
        AppEntry saved = Saved();
        Assert.Equal("My Tool", saved.Name);
        Assert.True(saved.Prerelease);
        Assert.Equal(["download", "page"], saved.Update);
        Assert.Contains("Track pre-releases / nightlies too?", rig.Prompts.Asked);
        Assert.Contains("  Added My Tool. Edit wintainium-apps.json to change how it updates.", rig.Log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_installed_mod_folder_is_tracked_by_release()
    {
        string mod = rig.Dir.Write("Mods/Pack/patches/b.ips", "x");
        rig.Dir.Write("Mods/Pack/a.txt", "x");
        latest = Release("v3");
        Answers("o/pack", "", rig.Dir.File("Mods/Pack"));
        rig.Prompts.Confirms.Enqueue(false); // pre-releases
        rig.Prompts.Confirms.Enqueue(true); // the latest is installed
        await Flow().RunAsync();
        AppEntry saved = Saved();
        Assert.Equal(mod, saved.Exe);
        Assert.Equal(rig.Dir.File("Mods/Pack"), saved.InstallDir);
        Assert.Equal("v3", saved.InstalledTag);
        Assert.Equal(["zip", "page"], saved.Update);
        Assert.Contains("Is the latest release (v3) the one in that folder?", rig.Prompts.YesDefaults);
    }

    [Fact]
    public async Task A_mod_folder_not_on_the_latest_release_is_unknown()
    {
        rig.Dir.Write("Mods/Pack/a.txt", "x");
        latest = Release("v3");
        Answers("o/pack", "", rig.Dir.File("Mods/Pack"));
        await Flow().RunAsync();
        AppEntry saved = Saved();
        Assert.Equal("unknown", saved.InstalledTag);
        Assert.Equal(rig.Dir.File("Mods/Pack/a.txt"), saved.Exe);
    }

    [Fact]
    public async Task An_empty_mod_folder_is_refused()
    {
        Directory.CreateDirectory(rig.Dir.File("Mods/Empty"));
        latest = Release("v3");
        Answers("o/pack", "", rig.Dir.File("Mods/Empty"));
        Assert.Null(await Flow().RunAsync());
        Assert.Contains("That folder is empty, so I am not adding it.", rig.Log.AllText, StringComparison.Ordinal);
        Assert.Empty(list.Entries);
    }

    [Fact]
    public async Task Release_errors_are_reported()
    {
        Answers("o/tool", "", "");
        Assert.Null(await new AddApp(rig.Ctx, new Updater(rig.Ctx, list), list, rig.Dir.File("Apps"), (_, _) => throw new LookupException("GitHub said 404")).RunAsync());
        Assert.Contains(("  Could not read the releases: GitHub said 404", LogColor.Red), rig.Log.Lines);
    }

    [Fact]
    public async Task Install_now_unpacks_into_the_default_folder_and_saves_the_pattern()
    {
        latest = Release("v1.4.2", "Tool-1.4.2-linux.AppImage", "Tool-1.4.2-windows-x64.zip");
        Answers("o/tool", "Tool", "", "");
        await Flow().RunAsync();
        string dir = rig.Dir.File("Apps/Tool");
        AppEntry saved = Saved();
        Assert.Equal(Path.Combine(dir, "tool.exe"), saved.Exe);
        Assert.Equal(@"^Tool-.+-windows-x64\.zip$", saved.Asset);
        Assert.Equal(["zip", "page"], saved.Update);
        Assert.False(saved.HasInstalledTag);
        Assert.Contains("  Latest release: v1.4.2", rig.Log.AllText, StringComparison.Ordinal);
        Assert.Contains($"  Installed into {dir}", rig.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Prompts.Asked, q => q.StartsWith("Which file?", StringComparison.Ordinal)); // the AppImage isn't offered
    }

    [Fact]
    public async Task Install_now_copies_a_non_archive()
    {
        latest = Release("v1.4.2", "Setup-1.4.2.exe");
        Answers("o/tool", "Tool", "", rig.Dir.File("Here"));
        await Flow().RunAsync();
        AppEntry saved = Saved();
        Assert.Equal(rig.Dir.File("Here/Setup-1.4.2.exe"), saved.Exe);
        Assert.Equal(["download", "page"], saved.Update);
        Assert.Equal(@"^Setup-.+\.exe$", saved.Asset);
    }

    [Fact]
    public async Task A_folder_with_files_is_only_used_after_asking()
    {
        rig.Dir.Write("Apps/Tool/old.txt", "x");
        latest = Release("v1.4.2", "Tool-1.4.2-windows-x64.zip");
        Answers("o/tool", "Tool", "", "");
        Assert.Null(await Flow().RunAsync());
        Assert.Contains($"{rig.Dir.File("Apps/Tool")} already has files in it. Install into it anyway?", rig.Prompts.Asked);
        Assert.Contains("  Cancelled.", rig.Log.AllText, StringComparison.Ordinal);
        Assert.Empty(rig.Http.Requests);
    }

    [Fact]
    public async Task Several_programs_ask_which_is_main()
    {
        latest = Release("v1.0", "Two-1.0.zip");
        Answers("o/two", "Two", "", "");
        rig.Prompts.Confirms.Enqueue(false); // pre-releases
        rig.Prompts.Choices.Enqueue(1);
        await Flow().RunAsync();
        Assert.Contains($"Which file is the main program?: a.exe | {Path.Combine("sub", "b.exe")}", rig.Prompts.Asked);
        Assert.Equal(rig.Dir.File("Apps/Two/sub/b.exe"), Saved().Exe);
    }

    [Fact]
    public async Task A_data_pack_is_tracked_by_release()
    {
        latest = Release("v3", "Pack-v3.zip");
        Answers("o/pack", "Pack", "", "");
        rig.Prompts.Confirms.Enqueue(false); // pre-releases
        rig.Prompts.Confirms.Enqueue(true); // it is a mod or data pack
        await Flow().RunAsync();
        AppEntry saved = Saved();
        string dir = rig.Dir.File("Apps/Pack");
        Assert.Equal(Path.Combine(dir, "patches", "a.ips"), saved.Exe);
        Assert.Equal(dir, saved.InstallDir);
        Assert.Equal("v3", saved.InstalledTag);
        Assert.Contains("  Tracking it by release: v3 is installed. Wintainium flags the next one.", rig.Log.AllText, StringComparison.Ordinal);
        Assert.Contains("There is no .exe in it. Is it a mod or data pack, to track by release?", rig.Prompts.YesDefaults);
    }

    [Fact]
    public async Task An_install_folder_that_is_a_file_is_refused_before_downloading()
    {
        string file = rig.Dir.Write("Apps/Tool", "a file");
        latest = Release("v1.4.2", "Tool-1.4.2-windows-x64.zip");
        Answers("o/tool", "Tool", "", "");
        Assert.Null(await Flow().RunAsync());
        Assert.Contains(($"  {file} is a file, not a folder. Not adding Tool.", LogColor.Red), rig.Log.Lines);
        Assert.Empty(rig.Http.Requests);
    }

    [Fact]
    public async Task A_failed_save_leaves_the_list_as_it_was()
    {
        Answers("o/tool", "Tool", @"C:\Tools\tool.exe");
        File.Delete(list.FilePath);
        Directory.CreateDirectory(list.FilePath); // the list file can't be written now
        await Assert.ThrowsAnyAsync<IOException>(() => Flow().RunAsync());
        Assert.Empty(list.Entries);
    }

    [Fact]
    public async Task No_exe_and_not_a_pack_uses_the_top_files()
    {
        latest = Release("v3", "Pack-v3.zip");
        Answers("o/pack", "Pack", "", "");
        await Flow().RunAsync();
        Assert.Equal(rig.Dir.File("Apps/Pack/readme.txt"), Saved().Exe);
        Assert.Equal(["zip", "page"], Saved().Update);
    }

    // ---- Cancel stops Add app (v0.3.0 took Cancel as Enter) ----

    [Fact]
    public async Task Cancel_on_the_link_stops()
    {
        rig.Prompts.Answers.Enqueue(null);
        Assert.Null(await Flow().RunAsync());
        Assert.Contains("  Cancelled.", rig.Log.AllText, StringComparison.Ordinal);
        Assert.Single(rig.Prompts.Asked);
    }

    [Fact]
    public async Task Cancel_on_the_install_folder_installs_nothing()
    {
        latest = Release("v1.4.2", "Tool-1.4.2-windows-x64.zip");
        Answers("o/tool", "Tool", "");
        rig.Prompts.Answers.Enqueue(null);
        Assert.Null(await Flow().RunAsync());
        Assert.Empty(rig.Http.Requests);
        Assert.False(Directory.Exists(rig.Dir.File("Apps/Tool")));
        Assert.Empty(list.Entries);
    }

    [Fact]
    public async Task Cancel_after_installing_removes_what_it_installed_but_not_files_that_were_there()
    {
        rig.Dir.Write("Apps/Two/mine.txt", "keep");
        latest = Release("v1.0", "Two-1.0.zip");
        Answers("o/two", "Two", "", "");
        rig.Prompts.Confirms.Enqueue(false); // pre-releases
        rig.Prompts.Confirms.Enqueue(true); // install into the folder that has files
        Assert.Null(await Flow().RunAsync()); // "Which file is the main program?" cancelled
        Assert.Empty(list.Entries);
        Assert.Equal("keep", File.ReadAllText(rig.Dir.File("Apps/Two/mine.txt")));
        Assert.False(File.Exists(rig.Dir.File("Apps/Two/a.exe")));
        Assert.False(Directory.Exists(rig.Dir.File("Apps/Two/sub")));
        Assert.Empty(Directory.GetFiles(rig.Ctx.DownloadDir));
        Assert.Contains("  Removed the 2 file(s) it had installed.", rig.Log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_program_removed_by_windows_security_is_not_added()
    {
        latest = Release("v1.4.2", "Tool-1.4.2-windows-x64.zip");
        Answers("o/tool", "Tool", "", "");
        var ctx = rig.Rebuild();
        var flow = new AddApp(
            new InstallContext
            {
                Http = ctx.Http,
                Log = ctx.Log,
                Prompts = ctx.Prompts,
                Shell = ctx.Shell,
                Mark = ctx.Mark,
                Threats = ctx.Threats,
                Processes = ctx.Processes,
                DownloadDir = ctx.DownloadDir,
                TempDir = ctx.TempDir,
                Delay = (_, _) =>
                {
                    File.Delete(rig.Dir.File("Apps/Tool/tool.exe"));
                    return Task.CompletedTask;
                },
            },
            new Updater(ctx, list),
            list,
            rig.Dir.File("Apps"),
            (_, _) => Task.FromResult<ReleaseInfo?>(latest));
        Assert.Null(await flow.RunAsync());
        Assert.Contains(("  Not adding Tool.", LogColor.Red), rig.Log.Lines);
        Assert.Empty(list.Entries);
    }
}
