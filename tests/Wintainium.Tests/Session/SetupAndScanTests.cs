using System.Net;
using System.Text.Json.Nodes;
using Wintainium.Core.Data;
using Wintainium.Core.Logging;
using Wintainium.Core.Scan;
using Wintainium.Core.Session;
using Wintainium.Core.Token;
using Wintainium.Tests.Helpers;

namespace Wintainium.Tests.Session;

/// <summary>The setup wizard's texts and saving (N-xx) and the Scan window's picking (C-xx).</summary>
public sealed class SetupAndScanTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly FakeHttp http = new();
    private readonly HttpClient client;
    private readonly MemoryLog log = new();

    public SetupAndScanTests() => client = http.Client();

    public void Dispose()
    {
        client.Dispose();
        http.Dispose();
        dir.Dispose();
    }

    private AppSession Open() => AppSession.Open(dir.Path, log, new FakeProtector(), client);

    [Fact]
    public void Wizard_texts()
    {
        Assert.Equal("Step 3 of 5", SetupSteps.StepLabel(3));
        Assert.Equal("No token saved. You can skip this with Next and add one later with Setup.", SetupSteps.TokenStatus(false, null));
        Assert.Equal("A token is already saved (expires 2027-01-31). Paste a new one only if you want to replace it, or click Next.", SetupSteps.TokenStatus(true, new DateTime(2027, 1, 31)));
        Assert.Contains("(no expiry date recorded)", SetupSteps.TokenStatus(true, null), StringComparison.Ordinal);
        Assert.Equal("Added 1 app to your list. Click Next.", SetupSteps.ScanResult(1));
        Assert.Equal("Added 3 apps to your list. Click Next.", SetupSteps.ScanResult(3));
        Assert.Equal("Nothing added. You can scan again any time with the Scan button.", SetupSteps.ScanResult(0));
    }

    [Fact]
    public void Finish_saves_folders_and_setup_done()
    {
        using AppSession s = Open();
        SetupSteps.Finish(s, @"  D:\Apps ", "");
        Settings saved = Settings.Load(s.Data.SettingsFile);
        Assert.True(saved.SetupDone);
        Assert.Equal(@"D:\Apps", saved.InstallRoot);
        Assert.Equal(s.DownloadDir, saved.DownloadDir);
        Assert.Contains(($"Setup saved. New apps go to D:\\Apps, downloads to {s.DownloadDir}.", LogColor.Green), log.Lines);
    }

    [Fact]
    public void Closing_the_wizard_still_counts_as_seen()
    {
        using AppSession s = Open();
        Assert.False(s.Settings.SetupDone);
        SetupSteps.MarkSeen(s);
        Assert.True(Settings.Load(s.Data.SettingsFile).SetupDone);
    }

    private static HttpResponseMessage RateLimit(string? scopes, string? expiry)
    {
        HttpResponseMessage r = FakeHttp.Json("""{ "rate": { "limit": 5000 } }""");
        if (scopes is not null)
        {
            r.Headers.Add("X-OAuth-Scopes", scopes);
        }

        if (expiry is not null)
        {
            r.Headers.Add("github-authentication-token-expiration", expiry);
        }

        return r;
    }

    [Fact]
    public async Task A_safe_token_is_saved()
    {
        http.On(GitHubTokenCheck.RateLimitUrl, _ => RateLimit("", "2027-01-31 10:00:00 UTC"));
        using AppSession s = Open();
        (string text, bool saved) = await SetupSteps.CheckAndSaveTokenAsync(client, s.Token, "ghp_" + new string('a', 36));
        Assert.True(saved);
        Assert.StartsWith("Saved. Token accepted: no permissions, read-only public access. GitHub now allows 5000 lookups an hour. It expires 2027-01-31.", text, StringComparison.Ordinal);
        Assert.True(s.Token.HasSavedToken);
    }

    [Fact]
    public async Task A_scoped_token_is_not_saved()
    {
        http.On(GitHubTokenCheck.RateLimitUrl, _ => RateLimit("repo", null));
        using AppSession s = Open();
        (string text, bool saved) = await SetupSteps.CheckAndSaveTokenAsync(client, s.Token, "ghp_" + new string('a', 36));
        Assert.False(saved);
        Assert.EndsWith("Nothing was saved.", text, StringComparison.Ordinal);
        Assert.False(s.Token.HasSavedToken);
    }

    // ---- scan picking ----

    private static ScanCandidate Cand(string name, string exe, CandidateKind kind = CandidateKind.Found, bool selected = true, string note = "", string key = "") =>
        new(kind, name, "From GitHub", exe, new JsonObject { ["name"] = name, ["exe"] = exe, ["source"] = "github", ["repo"] = "o/" + name }, selected, note, key);

    [Fact]
    public void Summary_texts()
    {
        Assert.Equal((ScanPicker.NothingNew, true), ScanPicker.Summary([]));
        Assert.Equal(("Found 1 app.", false), ScanPicker.Summary([new ScanPick(Cand("A", "a.exe"))]));
        var two = new[] { new ScanPick(Cand("A", "", CandidateKind.Download)), new ScanPick(Cand("B", "", CandidateKind.Download)) };
        Assert.Equal("Found 2 apps. 2 were downloaded but not found installed: use Locate .exe to point at them, leave them unticked, or click Don't show again.", ScanPicker.Summary(two).Text);
        Assert.Contains("1 was downloaded but not found installed: use Locate .exe to point at it, leave it unticked", ScanPicker.Summary([two[0]]).Text, StringComparison.Ordinal);
        Assert.Equal("Add selected", ScanPicker.AddLabel(0));
        Assert.Equal("Add selected (2)", ScanPicker.AddLabel(2));
        Assert.Equal("Downloaded x.zip", ScanPicker.LocateNote("Downloaded x.zip; click Locate to pick the installed .exe"));
    }

    [Fact]
    public void Footer_lists_mods_and_extra_folders()
    {
        Assert.Equal("", ScanPicker.Footer(0, []));
        Assert.Equal("Also found 2 mod or texture-pack folder(s) from code-hosting sites. To track one, use Add app with the mod's link and choose its folder.\nAlso scanned: D:\\Games, E:\\X", ScanPicker.Footer(2, [@"D:\Games", @"E:\X"]));
    }

    [Fact]
    public void Add_selected_adds_ticked_rows_with_a_program_and_unique_names()
    {
        using AppSession s = Open();
        s.List.Add(new AppEntry(new JsonObject { ["name"] = "Tool", ["exe"] = "t.exe" }));
        var rows = new[]
        {
            new ScanPick(Cand("Tool", "t2.exe")),
            new ScanPick(Cand("Off", "o.exe", selected: false)),
            new ScanPick(Cand("NoExe", "", CandidateKind.Download, selected: false)),
            new ScanPick(Cand("Renamed", "r.exe")) { Name = "  My Name  " },
            new ScanPick(Cand("Blank", "b.exe")) { Name = "   " },
        };
        rows[2].Checked = true; // ticked but never located: skipped
        Assert.Equal(3, ScanPicker.AddSelected(rows, s.List, log));
        Assert.Equal(["Tool", "Tool (2)", "My Name", "Blank"], AppList.Load(s.Data.AppsFile, log).Entries.Select(e => e.Name));
        Assert.Contains(("Added 3 apps from the scan.", LogColor.Green), log.Lines);
    }

    [Fact]
    public void Nothing_ticked_saves_nothing()
    {
        using AppSession s = Open();
        DateTime before = File.GetLastWriteTimeUtc(s.Data.AppsFile);
        Assert.Equal(0, ScanPicker.AddSelected([new ScanPick(Cand("A", "a.exe", selected: false))], s.List, log));
        Assert.DoesNotContain(log.Lines, l => l.Text.Contains("from the scan", StringComparison.Ordinal));
        Assert.Equal(before, File.GetLastWriteTimeUtc(s.Data.AppsFile));
    }

    [Fact]
    public void Locate_fills_the_program_and_ticks_the_row()
    {
        var r = new ScanPick(Cand("D", "", CandidateKind.Download, selected: false));
        Assert.True(r.NeedsLocate);
        r.Locate(@"C:\D\d.exe");
        Assert.False(r.NeedsLocate);
        Assert.True(r.Checked);
        Assert.Equal(@"C:\D\d.exe", r.Def["exe"]!.GetValue<string>());
    }

    [Fact]
    public void Hide_and_show_hidden_edit_scan_ignore()
    {
        using AppSession s = Open();
        ScanPicker.Hide(new ScanPick(Cand("D", "", CandidateKind.Download, key: "gh:o/d")), s.Settings);
        ScanPicker.Hide(new ScanPick(Cand("D", "", CandidateKind.Download, key: "gh:o/d")), s.Settings);
        ScanPicker.Hide(new ScanPick(Cand("E", "", CandidateKind.Download, key: "gh:o/e")), s.Settings);
        Assert.Equal(["gh:o/d", "gh:o/e"], Settings.Load(s.Data.SettingsFile).ScanIgnore);
        ScanPicker.ShowHidden(s.Settings);
        Assert.Empty(Settings.Load(s.Data.SettingsFile).ScanIgnore);
    }
}
