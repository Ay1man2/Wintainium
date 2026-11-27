using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Wintainium.Core.Data;
using Wintainium.Core.Logging;
using Wintainium.Core.Releases;
using Wintainium.Core.Status;
using Wintainium.Tests.Helpers;

namespace Wintainium.Tests.Status;

/// <summary>The checks of Test-Status.ps1: real files on disk, a mocked network, all sources.</summary>
public sealed class StatusTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly IReadOnlyList<AppRow> rows;
    private readonly string selfVersion = "0.4.0";

    public StatusTests()
    {
        DateTime nowUtc = DateTime.UtcNow;
        DateTime old = nowUtc.AddDays(-60);
        string Exe(string rel, DateTime when)
        {
            string p = dir.Write(rel, "x");
            File.SetLastWriteTimeUtc(p, when);
            return p;
        }

        string iso(DateTime d) => d.ToString("o", CultureInfo.InvariantCulture);
        string self = Exe("app/Wintainium.exe", old);
        var apps = new JsonArray
        {
            new JsonObject { ["name"] = "GhOld", ["exe"] = Exe("a/ghold.exe", old), ["source"] = "github", ["repo"] = "o/ghold", ["update"] = new JsonArray("download", "page") },
            new JsonObject { ["name"] = "GhNew", ["exe"] = Exe("a/ghnew.exe", nowUtc), ["source"] = "github", ["repo"] = "o/ghnew", ["update"] = new JsonArray("page") },
            new JsonObject { ["name"] = "Rolling", ["exe"] = Exe("a/roll.exe", old), ["source"] = "github", ["repo"] = "o/roll", ["tag"] = "latest", ["graceDays"] = 30 },
            new JsonObject { ["name"] = "Pre", ["exe"] = Exe("a/pre.exe", old), ["source"] = "github", ["repo"] = "o/pre", ["prerelease"] = true },
            new JsonObject { ["name"] = "Forgejo", ["exe"] = Exe("a/fj.exe", nowUtc), ["source"] = "gitea", ["api"] = "https://git.example/api/v1/repos/x/fj/releases/latest" },
            new JsonObject { ["name"] = "Lab", ["exe"] = Exe("a/lab.exe", old), ["source"] = "gitlab", ["api"] = "https://gitlab.com/api/v4/projects/x%2Flab/releases/permalink/latest" },
            new JsonObject { ["name"] = "Dol", ["exe"] = Exe("a/dol.exe", nowUtc), ["source"] = "dolphin", ["api"] = "https://dolphin-emu.org/update/latest/beta/" },
            new JsonObject { ["name"] = "Manual", ["exe"] = Exe("a/man.exe", nowUtc), ["source"] = "manual", ["url"] = "https://x" },
            new JsonObject { ["name"] = "Missing", ["exe"] = dir.File("a/nope.exe"), ["source"] = "github", ["repo"] = "o/ghnew" },
            new JsonObject { ["name"] = "Broken", ["exe"] = Exe("a/brk.exe", nowUtc), ["source"] = "github", ["repo"] = "o/broken" },
            new JsonObject { ["name"] = "Limited", ["exe"] = Exe("a/lim.exe", nowUtc), ["source"] = "github", ["repo"] = "o/limited" },
            new JsonObject { ["name"] = "Self", ["exe"] = self, ["source"] = "github", ["repo"] = "o/self" },
            new JsonObject { ["name"] = "SelfOld", ["exe"] = self, ["source"] = "github", ["repo"] = "o/selfnext" },
        };

        string now = iso(nowUtc);
        var http = new FakeHttp()
            .OnJson("https://api.github.com/repos/o/ghold/releases/latest", $$"""{ "tag_name": "v2.0", "published_at": "{{now}}", "html_url": "https://gh/ghold", "body": "notes", "assets": [ { "name": "ghold-2.0-win64.zip", "browser_download_url": "https://dl/ghold-2.0-win64.zip", "updated_at": "{{now}}" } ] }""")
            .OnJson("https://api.github.com/repos/o/self/releases/latest", $$"""{ "tag_name": "v{{selfVersion}}", "published_at": "{{now}}", "html_url": "https://gh/self", "assets": [] }""")
            .OnJson("https://api.github.com/repos/o/selfnext/releases/latest", $$"""{ "tag_name": "v9.0.0", "published_at": "{{iso(nowUtc.AddDays(-1))}}", "html_url": "https://gh/selfnext", "assets": [] }""")
            .OnJson("https://api.github.com/repos/o/ghnew/releases/latest", $$"""{ "tag_name": "v1.0", "published_at": "{{iso(nowUtc.AddDays(-10))}}", "html_url": "https://gh/ghnew", "assets": [] }""")
            .OnJson("https://api.github.com/repos/o/roll/releases/tags/latest", $$"""{ "tag_name": "latest", "published_at": "2020-01-01T00:00:00Z", "html_url": "https://gh/roll", "assets": [ { "name": "r.zip", "browser_download_url": "https://dl/r.zip", "updated_at": "{{now}}" } ] }""")
            .OnJson("https://api.github.com/repos/o/pre/releases?per_page=10", $$"""[ { "tag_name": "nightly-5", "draft": false, "prerelease": true, "published_at": "{{now}}", "html_url": "https://gh/pre", "assets": [] } ]""")
            .OnJson("https://git.example/api/v1/repos/x/fj/releases/latest", $$"""{ "tag_name": "v3.0", "published_at": "{{iso(nowUtc.AddDays(-1))}}", "html_url": "https://fj/rel", "assets": [] }""")
            .OnJson("https://gitlab.com/api/v4/projects/x%2Flab/releases/permalink/latest", $$"""{ "tag_name": "v9.9", "released_at": "{{now}}", "_links": { "self": "https://gl/rel" }, "assets": { "links": [ { "name": "Lab_9.9-x64.exe", "url": "https://gl/dl/1" } ] } }""")
            .OnJson("https://dolphin-emu.org/update/latest/beta/", $$"""{ "shortrev": "2609b", "date": "{{iso(nowUtc.AddDays(-1))}}", "artifacts": [ { "system": "Windows x64", "url": "https://dl.dolphin/dolphin-2609b-x64.7z" } ] }""")
            .OnStatus("https://api.github.com/repos/o/broken/releases/latest", HttpStatusCode.NotFound)
            .OnJson("https://api.github.com/repos/o/limited/releases/latest", """{"message":"API rate limit exceeded"}""", HttpStatusCode.Forbidden);

        using HttpClient client = http.Client();
        var cache = ReleaseCache.Load(dir.File(DataFolder.CacheFileName));
        var lookup = new ReleaseLookup(new ReleaseSources(client), cache, 60);
        var builder = new AppRows(lookup, new FakeVersionProbe(), new StatusEngine.SelfInfo(dir.File("app"), selfVersion));
        IReadOnlyList<AppEntry> entries = [.. apps.Select(a => new AppEntry(a!.AsObject()))];
        rows = builder.BuildAsync(entries, Settings.Load(dir.File(DataFolder.SettingsFileName)), force: false).GetAwaiter().GetResult();
    }

    /// <summary>Real file dates, no version resources (the fake programs are text files).</summary>
    private sealed class FakeVersionProbe : IExeProbe
    {
        public ExeInfo Probe(string path) =>
            File.Exists(path) ? new ExeInfo(true, "", File.GetLastWriteTimeUtc(path)) : ExeInfo.Missing;
    }

    public void Dispose() => dir.Dispose();

    [Fact]
    public void An_app_with_no_lookup_answer_is_lookup_failed_not_blank()
    {
        var app = new Wintainium.Core.Data.AppEntry(JsonNode.Parse("""{ "name": "NoAnswer", "exe": "C:/x/n.exe", "source": "github", "repo": "o/n" }""")!.AsObject());
        AppRow row = StatusEngine.BuildRow(app, null, new ExeInfo(true, "1.0.1", DateTime.UtcNow), 3, goldSelf: false);
        Assert.Equal(Statuses.LookupFailed, row.Status);
    }

    private AppRow Row(string name) => rows.Single(r => r.App == name);

    [Fact(DisplayName = "GhOld flagged UPDATE")]
    public void GhOld_flagged_update() => Assert.Equal(Statuses.Update, Row("GhOld").Status);

    [Fact(DisplayName = "GhNew up to date")]
    public void GhNew_up_to_date() => Assert.Equal(Statuses.UpToDate, Row("GhNew").Status);

    [Fact(DisplayName = "Rolling tag uses asset date, UPDATE past 30d grace")]
    public void Rolling_tag_uses_asset_date() => Assert.Equal(Statuses.Update, Row("Rolling").Status);

    [Fact(DisplayName = "Pre-release tracked")]
    public void Prerelease_tracked() => Assert.Equal("nightly-5", Row("Pre").Latest);

    [Fact(DisplayName = "Forgejo/Gitea source resolves")]
    public void Gitea_resolves()
    {
        Assert.Equal("v3.0", Row("Forgejo").Latest);
        Assert.Equal(Statuses.UpToDate, Row("Forgejo").Status);
    }

    [Fact(DisplayName = "GitLab source resolves + UPDATE")]
    public void GitLab_resolves()
    {
        Assert.Equal("v9.9", Row("Lab").Latest);
        Assert.Equal(Statuses.Update, Row("Lab").Status);
    }

    [Fact(DisplayName = "Dolphin feed resolves")]
    public void Dolphin_resolves() => Assert.Contains("2609b", Row("Dol").Latest, StringComparison.Ordinal);

    [Fact(DisplayName = "Manual shows check manually")]
    public void Manual_check_manually() => Assert.Equal(Statuses.CheckManually, Row("Manual").Status);

    [Fact(DisplayName = "Missing exe reported")]
    public void Missing_exe_reported() => Assert.Equal(Statuses.ExeNotFound, Row("Missing").Status);

    [Fact(DisplayName = "404 shows lookup failed")]
    public void NotFound_lookup_failed() => Assert.Equal(Statuses.LookupFailed, Row("Broken").Status);

    [Fact(DisplayName = "403 shows rate limited")]
    public void Forbidden_rate_limited() => Assert.Equal(Statuses.RateLimited, Row("Limited").Status);

    [Fact(DisplayName = "Wintainium reports its own version, up to date")]
    public void Self_reports_own_version()
    {
        Assert.Equal(selfVersion, Row("Self").Installed);
        Assert.Equal(Statuses.UpToDate, Row("Self").Status);
    }

    [Fact(DisplayName = "Wintainium flags its own newer release")]
    public void Self_flags_newer_release() => Assert.Equal(Statuses.Update, Row("SelfOld").Status);

    [Fact(DisplayName = "Rows: UPDATE, problems, exe not found, check manually, up to date; then by name")]
    public void Rows_in_order()
    {
        string[] order = [Statuses.Update, Statuses.RateLimited, Statuses.ExeNotFound, Statuses.CheckManually, Statuses.UpToDate];
        int Group(string s) => s == Statuses.LookupFailed ? 1 : Array.IndexOf(order, s);
        var groups = rows.Select(r => Group(r.Status)).ToList();
        Assert.Equal(groups.Order(), groups);
        var updates = rows.Where(r => r.Status == Statuses.Update).Select(r => r.App).ToList();
        Assert.Equal(updates.Order(StringComparer.CurrentCultureIgnoreCase), updates);
    }

    [Fact(DisplayName = "Update via is the first method's label; default is release page")]
    public void Via_labels()
    {
        Assert.Equal("download", Row("GhOld").Via);
        Assert.Equal("release page", Row("Pre").Via);
        Assert.Equal("EmuDeck", StatusEngine.MethodLabel("emudeck"));
        Assert.Equal("in-app", StatusEngine.MethodLabel("inapp"));
        Assert.Equal("installer", StatusEngine.MethodLabel("installer"));
        Assert.Equal("zip install", StatusEngine.MethodLabel("zip"));
        Assert.Equal("", StatusEngine.MethodLabel("teleport"));
    }

    [Fact(DisplayName = "Installed falls back to the exe date when there is no version")]
    public void Installed_is_the_exe_date()
    {
        string want = File.GetLastWriteTime(dir.File("a/ghold.exe")).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Equal(want, Row("GhOld").Installed);
    }

    [Fact(DisplayName = "Released is the local release date")]
    public void Released_is_local_date() =>
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", Row("GhOld").Released);

}
