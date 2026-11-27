using System.Text;
using Wintainium.Core.Data;
using Wintainium.Core.Logging;
using Wintainium.Tests.Helpers;

namespace Wintainium.Tests.Data;

public class AppListTests
{
    private const string TwoApps = """
        [
          { "name": "PCSX2", "exe": "%APPDATA%\\PCSX2\\pcsx2.exe", "source": "github", "repo": "PCSX2/pcsx2",
            "myOwnField": { "kept": true }, "update": ["emudeck", "inapp", "page"] },
          { "name": "Tool", "exe": "C:\\Tools\\tool.exe", "source": "manual", "url": "https://example.org" }
        ]
        """;

    [Fact(DisplayName = "app list created next to the script")]
    public void Missing_list_is_created_empty_with_a_notice()
    {
        using var dir = new TempDir();
        var log = new MemoryLog();
        AppList list = AppList.Load(dir.File(DataFolder.AppsFileName), log);
        Assert.Empty(list.Entries);
        Assert.Equal("[]", File.ReadAllText(dir.File(DataFolder.AppsFileName)).Trim());
        Assert.Contains("No app list yet", log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_utf8_with_and_without_bom()
    {
        using var dir = new TempDir();
        string withBom = dir.WriteBytes("a.json", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(TwoApps)]);
        string noBom = dir.Write("b.json", TwoApps);
        Assert.Equal(2, AppList.Load(withBom, NullLog.Instance).Entries.Count);
        Assert.Equal(2, AppList.Load(noBom, NullLog.Instance).Entries.Count);
    }

    [Fact]
    public void One_entry_written_as_an_object_reads_as_a_list()
    {
        using var dir = new TempDir();
        string path = dir.Write("a.json", """{ "name": "Solo", "exe": "C:\\solo.exe" }""");
        AppList list = AppList.Load(path, NullLog.Instance);
        Assert.Equal("Solo", Assert.Single(list.Entries).Name);
    }

    [Fact]
    public void Unknown_fields_and_field_order_survive_a_save()
    {
        using var dir = new TempDir();
        string path = dir.Write("a.json", TwoApps);
        AppList list = AppList.Load(path, NullLog.Instance);
        list.Entries[0].InstalledTag = "v2";
        list.Save();

        string text = File.ReadAllText(path);
        Assert.Contains("\"myOwnField\"", text, StringComparison.Ordinal);
        Assert.Contains("\"kept\": true", text, StringComparison.Ordinal);
        int name = text.IndexOf("\"name\"", StringComparison.Ordinal);
        int exe = text.IndexOf("\"exe\"", StringComparison.Ordinal);
        int own = text.IndexOf("\"myOwnField\"", StringComparison.Ordinal);
        int tag = text.IndexOf("\"installedTag\"", StringComparison.Ordinal);
        Assert.True(name < exe && exe < own && own < tag, "fields keep their order, new ones go last");
        Assert.Contains(@"%APPDATA%\\PCSX2\\pcsx2.exe", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Saves_utf8_without_bom_and_keeps_a_backup()
    {
        using var dir = new TempDir();
        string path = dir.Write("a.json", TwoApps);
        AppList list = AppList.Load(path, NullLog.Instance);
        list.Remove("Tool");
        list.Save();
        byte[] bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Contains("\"Tool\"", File.ReadAllText(path + ".bak"), StringComparison.Ordinal);
        Assert.Single(AppList.Load(path, NullLog.Instance).Entries);
    }

    [Fact]
    public void Broken_list_is_restored_from_the_backup()
    {
        using var dir = new TempDir();
        string path = dir.Write("a.json", "[ { \"name\": \"broken\" ");
        dir.Write("a.json.bak", TwoApps);
        var log = new MemoryLog();
        AppList list = AppList.Load(path, log);
        Assert.Equal(2, list.Entries.Count);
        Assert.Contains("Restored the previous copy", log.AllText, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(dir.Path, "a.json.broken-*"));
        Assert.Equal(2, AppList.Load(path, NullLog.Instance).Entries.Count);
    }

    [Fact]
    public void A_restore_keeps_the_good_backup()
    {
        using var dir = new TempDir();
        string path = dir.Write("a.json", "[ { \"name\": \"broken\" ");
        string bak = dir.Write("a.json.bak", TwoApps);
        AppList.Load(path, NullLog.Instance);
        Assert.Equal(TwoApps, File.ReadAllText(bak));

        // broken again before the next save: the backup still restores
        File.WriteAllText(path, "{ nope");
        Assert.Equal(2, AppList.Load(path, NullLog.Instance).Entries.Count);
    }

    [Fact]
    public void A_list_that_cannot_be_created_is_reported()
    {
        using var dir = new TempDir();
        string blocker = dir.Write("notafolder", "x");
        AppListLoadException ex = Assert.Throws<AppListLoadException>(() => AppList.Load(Path.Combine(blocker, "a.json"), NullLog.Instance));
        Assert.Contains("could not be read", ex.Message, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void An_access_denied_list_is_reported_not_crashed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        string path = dir.Write("a.json", TwoApps);
        using (new DeniedFile(path))
        {
            AppListLoadException ex = Assert.Throws<AppListLoadException>(() => AppList.Load(path, NullLog.Instance));
            Assert.Contains("could not be read", ex.Message, StringComparison.Ordinal);
        }

        dir.Write("a.json.bak", TwoApps);
        File.WriteAllText(path, "{ nope");
        string bak = path + ".bak";
        using (new DeniedFile(bak))
        {
            Assert.Throws<AppListLoadException>(() => AppList.Load(path, NullLog.Instance));
        }
    }

    [Fact]
    public void Broken_list_without_backup_is_reported_and_left_alone()
    {
        using var dir = new TempDir();
        const string broken = "[ { \"name\": ";
        string path = dir.Write("a.json", broken);
        AppListLoadException ex = Assert.Throws<AppListLoadException>(() => AppList.Load(path, NullLog.Instance));
        Assert.Contains("could not be read", ex.Message, StringComparison.Ordinal);
        Assert.Equal(broken, File.ReadAllText(path));
    }

    [Fact]
    public void Field_names_match_case_insensitively_like_PowerShell()
    {
        using var dir = new TempDir();
        string path = dir.Write("a.json", """[ { "Name": "X", "EXE": "C:\\x.exe", "Update": "zip" } ]""");
        AppEntry e = Assert.Single(AppList.Load(path, NullLog.Instance).Entries);
        Assert.Equal("X", e.Name);
        Assert.Equal(@"C:\x.exe", e.Exe);
        Assert.Equal(["zip"], e.Update);
        e.Name = "Y";
        Assert.Equal("Y", e.Json["Name"]!.GetValue<string>());
        Assert.False(e.Json.ContainsKey("name"));
    }

    [Fact(DisplayName = "every app field is read (D-03)")]
    public void Every_field_is_read()
    {
        var e = new AppEntry(System.Text.Json.Nodes.JsonNode.Parse("""
            { "name": "N", "exe": "e.exe", "source": "gitlab", "repo": "o/r", "tag": "t", "prerelease": true, "api": "https://api",
              "url": "https://url", "asset": "^a$", "downloadUrl": "https://dl", "update": ["emudeck", "page"], "emudeckFn": "X_install",
              "hint": "h", "launch": "l.exe", "graceDays": 5, "installDir": "D", "installedTag": "v1", "note": "n" }
            """)!.AsObject());
        Assert.Equal(
            ("N", "e.exe", "gitlab", "o/r", "t", true, "https://api", "https://url", "^a$", "https://dl"),
            (e.Name, e.Exe, e.Source, e.Repo, e.Tag, e.Prerelease, e.Api, e.Url, e.Asset, e.DownloadUrl));
        Assert.Equal(["emudeck", "page"], e.Update);
        Assert.Equal(("X_install", "h", "l.exe", 5, "D", "v1", "n"), (e.EmudeckFn, e.Hint, e.Launch, e.GraceDays, e.InstallDir, e.InstalledTag, e.Note));
    }

    [Fact(DisplayName = "environment variables in exe, launch and installDir are expanded (D-04)")]
    public void Environment_variables_are_expanded()
    {
        Environment.SetEnvironmentVariable("WINTAINIUM_TEST_ROOT", "R");
        var e = new AppEntry { Exe = "%WINTAINIUM_TEST_ROOT%/a.exe", Launch = "%WINTAINIUM_TEST_ROOT%/b.exe", InstallDir = "%WINTAINIUM_TEST_ROOT%/D" };
        Assert.Equal(("R/a.exe", "R/b.exe", "R/D"), (e.ExpandedExe, e.ExpandedLaunch, e.ExpandedInstallDir));
        Assert.Equal("%WINTAINIUM_TEST_ROOT%/a.exe", e.Exe); // saved as written
    }

    [Fact]
    public void Field_values_follow_PowerShell_truthiness()
    {
        var e = new AppEntry(System.Text.Json.Nodes.JsonNode.Parse(
            """{ "prerelease": "false", "graceDays": 0, "installedTag": "", "update": ["", "zip", null] }""")!.AsObject());
        Assert.True(e.Prerelease);          // a non-empty string is true in PowerShell
        Assert.Null(e.GraceDays);           // 0 means "use the default"
        Assert.False(e.HasInstalledTag);
        Assert.Equal(["zip"], e.Update);
        Assert.Equal(["page"], new AppEntry().Update);
    }

    [Fact(DisplayName = "remove: only that app removed")]
    public void Remove_takes_only_that_app()
    {
        using var dir = new TempDir();
        AppList list = AppList.Load(dir.Write("a.json", TwoApps), NullLog.Instance);
        Assert.Equal(1, list.Remove("pcsx2"));
        Assert.Equal("Tool", Assert.Single(list.Entries).Name);
    }

    [Fact]
    public void Unique_names_count_up()
    {
        using var dir = new TempDir();
        AppList list = AppList.Load(dir.Write("a.json", TwoApps), NullLog.Instance);
        Assert.Equal("New", list.UniqueName("New"));
        Assert.Equal("Tool (2)", list.UniqueName("Tool"));
        list.Add(new AppEntry { Name = "Tool (2)" });
        Assert.Equal("Tool (3)", list.UniqueName("Tool"));
    }

    [Fact]
    public void Self_entry_is_recognised_by_repo()
    {
        Assert.True(new AppEntry { Repo = "ay1man2/wintainium" }.IsSelf);
        Assert.False(new AppEntry { Repo = "someone/else" }.IsSelf);
    }

    [Fact]
    public void Legacy_files_are_copied_once()
    {
        using var dir = new TempDir();
        dir.Write("emu-apps.json", "[]");
        dir.Write("emu-cache.json", "{}");
        dir.Write(DataFolder.CacheFileName, "{\"keep\":1}");
        new DataFolder(dir.Path).MigrateLegacyFiles();
        Assert.Equal("[]", File.ReadAllText(dir.File(DataFolder.AppsFileName)));
        Assert.Equal("{\"keep\":1}", File.ReadAllText(dir.File(DataFolder.CacheFileName)));
        Assert.False(File.Exists(dir.File(DataFolder.TokenFileName)));
    }
}
