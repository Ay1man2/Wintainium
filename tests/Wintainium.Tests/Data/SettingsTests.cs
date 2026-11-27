using System.Text.Json.Nodes;
using Wintainium.Core.Data;
using Wintainium.Tests.Helpers;

namespace Wintainium.Tests.Data;

public class SettingsTests
{
    [Fact(DisplayName = "install folder from settings file")]
    public void Install_folder_from_settings_file()
    {
        using var dir = new TempDir();
        string path = dir.Write(DataFolder.SettingsFileName, """{ "installRoot": "D:\\MyApps", "downloadDir": "D:\\MyDownloads", "setupDone": true }""");
        Assert.Equal(@"D:\MyApps", Settings.Load(path).InstallRoot);
    }

    [Fact(DisplayName = "download folder from settings file")]
    public void Download_folder_from_settings_file()
    {
        using var dir = new TempDir();
        string path = dir.Write(DataFolder.SettingsFileName, """{ "installRoot": "D:\\MyApps", "downloadDir": "D:\\MyDownloads", "setupDone": true }""");
        Settings s = Settings.Load(path);
        Assert.Equal(@"D:\MyDownloads", s.DownloadDir);
        Assert.True(s.SetupDone);
    }

    [Fact]
    public void Missing_or_unreadable_settings_count_as_empty()
    {
        using var dir = new TempDir();
        foreach (Settings s in new[] { Settings.Load(dir.File("none.json")), Settings.Load(dir.Write("bad.json", "{ nope")) })
        {
            Assert.Null(s.InstallRoot);
            Assert.Null(s.DownloadDir);
            Assert.False(s.SetupDone);
            Assert.True(s.GoldSelf);
            Assert.Empty(s.ScanIgnore);
            Assert.Equal(3, s.GraceDays);
            Assert.Equal(60, s.CacheMinutes);
        }
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("\"false\"", false)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("null", true)]
    [InlineData("\"yes\"", true)]
    public void Gold_is_on_unless_turned_off(string value, bool gold)
    {
        using var dir = new TempDir();
        Assert.Equal(gold, Settings.Load(dir.Write("s.json", $$"""{ "goldSelf": {{value}} }""")).GoldSelf);
    }

    [Fact]
    public void Saving_keeps_other_keys_and_reloads()
    {
        using var dir = new TempDir();
        string path = dir.Write("s.json", """{ "installRoot": "D:\\Apps", "somethingNew": [1, 2] }""");
        Settings s = Settings.Load(path);
        s.Save("scanIgnore", new JsonArray("owner/repo", "https://x.itch.io/y"));
        s.Save("goldSelf", false);

        Settings again = Settings.Load(path);
        Assert.Equal(@"D:\Apps", again.InstallRoot);
        Assert.Equal(["owner/repo", "https://x.itch.io/y"], again.ScanIgnore);
        Assert.False(again.GoldSelf);
        Assert.NotNull(again["somethingNew"]);
    }

    [Fact]
    public void Grace_days_and_cache_minutes_can_be_set()
    {
        using var dir = new TempDir();
        Settings s = Settings.Load(dir.Write("s.json", """{ "graceDays": 7, "cacheMinutes": 15 }"""));
        Assert.Equal(7, s.GraceDays);
        Assert.Equal(15, s.CacheMinutes);
    }
}
