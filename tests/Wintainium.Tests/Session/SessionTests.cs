using System.Text.Json.Nodes;
using Wintainium.Core.Data;
using Wintainium.Core.Install;
using Wintainium.Core.Logging;
using Wintainium.Core.Releases;
using Wintainium.Core.Session;
using Wintainium.Core.Status;
using Wintainium.Tests.Helpers;

namespace Wintainium.Tests.Session;

public sealed class SessionTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly FakeHttp http = new();
    private readonly HttpClient client;
    private readonly MemoryLog log = new();

    public SessionTests() => client = http.Client();

    public void Dispose()
    {
        client.Dispose();
        http.Dispose();
        dir.Dispose();
    }

    private static AppRow Row(string json, string status = Statuses.UpToDate, string notes = "", string latest = "2.0", string released = "", string page = "")
    {
        var e = new AppEntry(JsonNode.Parse(json)!.AsObject());
        return new AppRow
        {
            Entry = e,
            Status = status,
            Installed = "1.0",
            Latest = latest,
            Released = released,
            Methods = ["zip"],
            Page = page,
            Exe = e.ExpandedExe,
            Notes = notes,
        };
    }

    private static string Esc(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    // ---- RowActions ----

    [Fact]
    public void No_selection_says_so()
    {
        Assert.Null(RowActions.Require(null, log));
        Assert.Equal((RowActions.SelectFirst, LogColor.Yellow), log.Lines.Single());
    }

    [Fact]
    public void Launch_starts_the_exe_in_its_folder()
    {
        string exe = dir.Write("A/a.exe", "x");
        var shell = new FakeShell();
        RowActions.Launch(Row($$"""{ "name": "A", "exe": "{{Esc(exe)}}" }"""), shell, log);
        Assert.Equal([exe], shell.Started);
        Assert.Contains("Starting A.", log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public void Launch_prefers_the_launch_field()
    {
        string exe = dir.Write("A/a.exe", "x");
        string launcher = dir.Write("A/go.bat", "x");
        var shell = new FakeShell();
        RowActions.Launch(Row($$"""{ "name": "A", "exe": "{{Esc(exe)}}", "launch": "{{Esc(launcher)}}" }"""), shell, log);
        Assert.Equal([launcher], shell.Started);
    }

    [Fact]
    public void Launch_of_a_mod_opens_its_folder()
    {
        string file = dir.Write("Mod/patches/p.ips", "x");
        var shell = new FakeShell();
        RowActions.Launch(Row($$"""{ "name": "Mod", "exe": "{{Esc(file)}}", "installDir": "{{Esc(dir.File("Mod"))}}" }"""), shell, log);
        Assert.Empty(shell.Started);
        Assert.Equal([dir.File("Mod")], shell.Shown);
        Assert.Contains("Mod has no program to start. Opening its folder.", log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public void Launch_of_a_missing_exe_says_not_found()
    {
        var shell = new FakeShell();
        string exe = dir.File("Gone/g.exe");
        RowActions.Launch(Row($$"""{ "name": "G", "exe": "{{Esc(exe)}}" }"""), shell, log);
        Assert.Empty(shell.Started);
        Assert.Equal(($"  Not found: {exe}", LogColor.Red), log.Lines.Single());
    }

    [Fact]
    public void Open_folder_or_says_it_is_missing_unless_quiet()
    {
        var shell = new FakeShell();
        string exe = dir.File("Gone/g.exe");
        AppRow row = Row($$"""{ "name": "G", "exe": "{{Esc(exe)}}" }""");
        RowActions.OpenFolder(row, shell, log, quietWhenMissing: true);
        Assert.Empty(log.Lines);
        RowActions.OpenFolder(row, shell, log);
        Assert.Equal(($"Folder not found: {dir.File("Gone")}", LogColor.Red), log.Lines.Single());

        string there = dir.Write("Here/h.exe", "x");
        RowActions.OpenFolder(Row($$"""{ "name": "H", "exe": "{{Esc(there)}}" }"""), shell, log);
        Assert.Equal([dir.File("Here")], shell.Shown);
    }

    [Fact]
    public void Release_page_opens_or_says_none_is_set()
    {
        var shell = new FakeShell();
        RowActions.OpenPage(Row("""{ "name": "A", "exe": "a.exe" }""", page: "https://example.org/r"), shell, log);
        Assert.Equal(["https://example.org/r"], shell.Opened);
        RowActions.OpenPage(Row("""{ "name": "B", "exe": "b.exe" }"""), shell, log, quietWhenMissing: true);
        Assert.Empty(log.Lines);
        RowActions.OpenPage(Row("""{ "name": "B", "exe": "b.exe" }"""), shell, log);
        Assert.Equal(("No release page is set for B.", LogColor.Yellow), log.Lines.Single());
    }

    [Fact]
    public void Release_page_never_opens_a_non_https_link()
    {
        var shell = new FakeShell();
        RowActions.OpenPage(Row("""{ "name": "A", "exe": "a.exe" }""", page: "file:///C:/Windows/notepad.exe"), shell, log);
        Assert.Empty(shell.Opened);
    }

    [Fact]
    public void Update_of_a_row_without_update_asks_first()
    {
        var prompts = new FakePrompts();
        Assert.True(RowActions.ConfirmUpdate(Row("""{ "name": "A", "exe": "a.exe" }""", Statuses.Update), prompts));
        Assert.Empty(prompts.Asked);
        Assert.False(RowActions.ConfirmUpdate(Row("""{ "name": "A", "exe": "a.exe" }"""), prompts));
        Assert.Equal("A is up to date. Run its update anyway?", prompts.Asked.Single());
        prompts.Confirms.Enqueue(true);
        Assert.True(RowActions.ConfirmUpdate(Row("""{ "name": "A", "exe": "a.exe" }"""), prompts));
    }

    [Fact]
    public void Remove_asks_then_drops_the_entry_and_its_cache_but_not_files()
    {
        string exe = dir.Write("A/a.exe", "x");
        dir.Write("wintainium-apps.json", $$"""[ { "name": "A", "exe": "{{Esc(exe)}}", "source": "manual" }, { "name": "B", "exe": "b.exe", "source": "manual" } ]""");
        using AppSession s = AppSession.Open(dir.Path, log, new FakeProtector(), client);
        s.Cache.Set("A", new ReleaseInfo { Tag = "1", Date = DateTime.UtcNow, Page = "", Assets = [], Notes = "" }, DateTime.UtcNow);
        s.Cache.Save();
        var prompts = new FakePrompts();
        AppRow row = Row($$"""{ "name": "A", "exe": "{{Esc(exe)}}" }""");

        Assert.False(RowActions.Remove(row, s, prompts));
        Assert.Equal("Remove A from the list? Its files stay where they are.", prompts.Asked.Single());
        Assert.Equal(2, s.List.Entries.Count);

        prompts.Confirms.Enqueue(true);
        Assert.True(RowActions.Remove(row, s, prompts));
        Assert.Equal(["B"], AppList.Load(s.Data.AppsFile, log).Entries.Select(e => e.Name));
        Assert.False(ReleaseCache.Load(s.Data.CacheFile).Remove("A"));
        Assert.True(File.Exists(exe));
        Assert.Contains("  Removed A from the list.", log.AllText, StringComparison.Ordinal);
    }

    // ---- RowViews ----

    [Theory]
    [InlineData("", false, true)]
    [InlineData("dolph", false, true)]
    [InlineData("DOLPH", false, true)]
    [InlineData("  dolph  ", false, true)]
    [InlineData("cemu", false, false)]
    [InlineData("", true, false)]
    public void Search_and_only_updates_filter_rows(string search, bool onlyUpdates, bool visible) =>
        Assert.Equal(visible, RowViews.IsVisible(Row("""{ "name": "Dolphin", "exe": "d.exe" }"""), search, onlyUpdates));

    [Fact]
    public void Only_updates_keeps_rows_that_need_attention()
    {
        Assert.True(RowViews.IsVisible(Row("""{ "name": "A", "exe": "a.exe" }""", Statuses.LookupFailed), null, true));
        Assert.True(RowViews.IsVisible(Row("""{ "name": "A", "exe": "a.exe" }""", Statuses.Update), null, true));
    }

    [Fact]
    public void Notes_strip_markdown_and_show_the_dates()
    {
        NotesView v = RowViews.Notes(Row("""{ "name": "A", "exe": "a.exe" }""", notes: "## Fixes\r\n- **big** fix\n### More", released: "2026-11-01", page: "https://example.org"));
        Assert.Equal("A  2.0", v.Title);
        Assert.Equal("Released 2026-11-01  -  you have 1.0", v.Subtitle);
        Assert.Equal("Fixes\n- big fix\nMore", v.Body);
        Assert.Equal("https://example.org", v.Page);
    }

    [Fact]
    public void Notes_fall_back_when_there_are_none()
    {
        Assert.Equal(RowViews.NoNotesCached, RowViews.Notes(Row("""{ "name": "A", "exe": "a.exe" }""")).Body);
        NotesView none = RowViews.Notes(Row("""{ "name": "A", "exe": "a.exe" }""", latest: ""));
        Assert.Equal(RowViews.NoReleaseInfo, none.Body);
        Assert.Equal("You have 1.0", none.Subtitle);
    }

    [Fact]
    public void Table_matches_the_v030_layout()
    {
        var rows = new[]
        {
            Row("""{ "name": "An app with a very long name indeed", "exe": "a.exe" }""", Statuses.Update, released: "2026-11-01"),
            Row("""{ "name": "B", "exe": "b.exe" }"""),
            Row("""{ "name": "C", "exe": "c.exe" }""", Statuses.LookupFailed),
        };
        IReadOnlyList<(string Text, LogColor Color)> t = RowViews.Table(rows);
        Assert.Equal(("", LogColor.Default), t[0]);
        Assert.Equal(("  #  App                       Status          Installed         Latest                    Released    Update via", LogColor.Cyan), t[1]);
        Assert.Equal(("  1  An app with a very lon... UPDATE          1.0               2.0                       2026-11-01  "+ StatusEngine.MethodLabel("zip"), LogColor.Yellow), t[2]);
        Assert.Equal(LogColor.DarkGray, t[3].Color);
        Assert.Equal(LogColor.Red, t[4].Color);
        Assert.Equal(("1 update(s) available.", LogColor.Cyan), t[^1]);
    }

    // ---- CommandLine ----

    [Fact]
    public void No_arguments_opens_the_window()
    {
        CommandLine c = CommandLine.Parse([]);
        Assert.Null(c.Error);
        Assert.False(c.List || c.Setup || c.Uninstall || c.InstallOnly);
        Assert.Null(c.Data);
    }

    [Theory]
    [InlineData(new[] { "--list" }, true, false, null)]
    [InlineData(new[] { "--LIST", "--setup" }, true, true, null)]
    [InlineData(new[] { "--data", "C:\\Temp\\w" }, false, false, "C:\\Temp\\w")]
    [InlineData(new[] { "--data=\"C:\\My Data\"" }, false, false, "C:\\My Data")]
    public void Options_are_read(string[] args, bool list, bool setup, string? data)
    {
        CommandLine c = CommandLine.Parse(args);
        Assert.Null(c.Error);
        Assert.Equal(list, c.List);
        Assert.Equal(setup, c.Setup);
        Assert.Equal(data, c.Data);
    }

    [Theory]
    [InlineData(new[] { "--data" }, "--data needs a folder.")]
    [InlineData(new[] { "--data", "--list" }, "--data needs a folder.")]
    [InlineData(new[] { "--data=" }, "--data needs a folder.")]
    [InlineData(new[] { "token" }, "Unknown option: token")]
    [InlineData(new[] { "--list", "-Config" }, "Unknown option: -Config")]
    [InlineData(new[] { "--list=yes" }, "--list doesn't take a value.")]
    [InlineData(new[] { "--Setup=1" }, "--Setup doesn't take a value.")]
    [InlineData(new[] { "--install-only", "--data", "D:\\W" }, "--install-only can't be used with --data.")]
    public void Bad_options_are_explained(string[] args, string error) => Assert.Equal(error, CommandLine.Parse(args).Error);

    [Fact]
    public void Uninstall_and_install_only_are_recognised()
    {
        Assert.True(CommandLine.Parse(["--uninstall"]).Uninstall);
        Assert.True(CommandLine.Parse(["--install-only"]).InstallOnly);
    }

    // ---- ChoiceAnswer ----

    [Theory]
    [InlineData("2", 3, "how? (Enter = 1, x = cancel)", 1)]
    [InlineData("", 3, "how? (Enter = 1, x = cancel)", 0)]
    [InlineData("9", 3, "how? (Enter = 1, x = cancel)", 0)]
    [InlineData("x", 3, "how? (Enter = 1, x = cancel)", null)]
    [InlineData("X", 3, "how? (Enter = 1, x = cancel)", null)]
    [InlineData(" 3 ", 3, "number (Enter to cancel)", 2)]
    [InlineData("", 3, "number (Enter to cancel)", null)]
    [InlineData("4", 3, "number (Enter to cancel)", null)]
    [InlineData("-1", 3, "number (Enter to cancel)", null)]
    public void Typed_choices_are_read_like_v030(string answer, int count, string prompt, int? expected) =>
        Assert.Equal(expected, ChoiceAnswer.Parse(answer, count, prompt));

    [Fact]
    public void Closing_the_choice_dialog_cancels() =>
        Assert.Null(ChoiceAnswer.Parse(null, 3, "how? (Enter = 1, x = cancel)"));

    // ---- AppSession ----

    [Fact]
    public void Opening_an_empty_folder_creates_an_empty_list()
    {
        using AppSession s = AppSession.Open(dir.File("new"), log, new FakeProtector(), client);
        Assert.Empty(s.List.Entries);
        Assert.True(File.Exists(s.Data.AppsFile));
    }

    [Fact]
    public async Task Refresh_reads_the_list_again_and_notes_come_separately()
    {
        string exe = dir.Write("M/m.exe", "x");
        using AppSession s = AppSession.Open(dir.Path, log, new FakeProtector(), client);
        Assert.Empty(await s.RefreshAsync(false));
        dir.Write("wintainium-apps.json", $$"""[ { "name": "Manual", "exe": "{{Esc(exe)}}", "source": "manual" } ]""");
        IReadOnlyList<AppRow> rows = await s.RefreshAsync(false);
        Assert.Equal(Statuses.CheckManually, Assert.Single(rows).Status);
        Assert.Same(rows, s.Rows);
        s.WriteLookupNotes();
    }

    [Fact]
    public void Gold_is_saved_and_applied_to_the_self_row()
    {
        using AppSession s = AppSession.Open(dir.Path, log, new FakeProtector(), client);
        RowActions.SetGold(s, false);
        Assert.False(Settings.Load(s.Data.SettingsFile).GoldSelf);
        RowActions.SetGold(s, true);
        Assert.True(Settings.Load(s.Data.SettingsFile).GoldSelf);
    }

    [Fact]
    public void Folders_default_and_follow_settings()
    {
        dir.Write("wintainium-settings.json", """{ "emuDeckExe": "D:\\Tools\\EmuDeck" }""");
        using AppSession s = AppSession.Open(dir.Path, log, new FakeProtector(), client);
        Assert.EndsWith("EmuDeck.exe", s.EmuDeckExe, StringComparison.Ordinal);
        Assert.StartsWith("D:\\Tools\\EmuDeck", s.EmuDeckExe, StringComparison.Ordinal);
        Assert.Contains("Wintainium", s.InstallRoot, StringComparison.Ordinal);
        Assert.Contains("Wintainium", s.DownloadDir, StringComparison.Ordinal);
    }
}
