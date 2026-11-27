using System.Net;
using Wintainium.Core.Data;
using Wintainium.Core.Releases;
using Wintainium.Tests.Helpers;

namespace Wintainium.Tests.Releases;

public class ReleaseSourcesTests
{
    private static AppEntry App(string json) => new(System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject());

    private static string Iso(DateTime d) => d.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task GitHub_latest_release()
    {
        var http = new FakeHttp().OnJson("https://api.github.com/repos/o/ok/releases/latest",
            """
                { "tag_name": "v1.0", "published_at": "2026-10-01T12:00:00Z", "html_url": "https://github.com/o/ok/releases/tag/v1.0", "body": "notes",
                                 "assets": [ { "name": "ok.zip", "browser_download_url": "https://github.com/o/ok/releases/download/v1.0/ok.zip" } ] }
                """);
        using HttpClient client = http.Client();
        ReleaseInfo r = (await new ReleaseSources(client).GetLatestAsync(App("""{ "name": "Ok", "source": "github", "repo": "o/ok" }""")))!;
        Assert.Equal("v1.0", r.Tag);
        Assert.Equal(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), r.Date);
        Assert.Equal("ok.zip", Assert.Single(r.Assets).Name);
        Assert.Equal("notes", r.Notes);
        HttpRequestMessage req = Assert.Single(http.Requests);
        Assert.Equal("Wintainium", req.Headers.UserAgent.ToString());
        Assert.Contains("application/vnd.github+json", req.Headers.Accept.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHub_rolling_tag_uses_the_newest_asset_date()
    {
        var http = new FakeHttp().OnJson("https://api.github.com/repos/o/roll/releases/tags/latest",
            """
                { "tag_name": "latest", "published_at": "2025-01-01T00:00:00Z", "html_url": "https://x",
                                 "assets": [ { "name": "a.zip", "browser_download_url": "https://x/a.zip", "updated_at": "2026-11-01T00:00:00Z" },
                                             { "name": "b.zip", "browser_download_url": "https://x/b.zip", "updated_at": "2026-11-20T00:00:00Z" } ] }
                """);
        using HttpClient client = http.Client();
        ReleaseInfo r = (await new ReleaseSources(client).GetLatestAsync(App("""{ "source": "github", "repo": "o/roll", "tag": "latest" }""")))!;
        Assert.Equal(new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc), r.Date);
    }

    [Fact]
    public async Task GitHub_prerelease_takes_the_newest_non_draft()
    {
        var http = new FakeHttp().OnJson("https://api.github.com/repos/o/pre/releases?per_page=10",
            """[ { "tag_name": "draft-6", "draft": true }, { "tag_name": "nightly-5", "draft": false, "published_at": "2026-11-01T00:00:00Z" }, { "tag_name": "v1" } ]""");
        using HttpClient client = http.Client();
        ReleaseInfo r = (await new ReleaseSources(client).GetLatestAsync(App("""{ "source": "github", "repo": "o/pre", "prerelease": true }""")))!;
        Assert.Equal("nightly-5", r.Tag);
    }

    [Fact]
    public async Task Gitea_Forgejo_source_resolves()
    {
        var http = new FakeHttp().OnJson("https://git.example/api/v1/repos/x/fj/releases/latest",
            """{ "tag_name": "v3.0", "published_at": "2026-10-01T00:00:00+02:00", "html_url": "https://git.example/x/fj/releases/tag/v3.0", "assets": [] }""");
        using HttpClient client = http.Client();
        ReleaseInfo r = (await new ReleaseSources(client, "ghp_secret").GetLatestAsync(App("""{ "source": "gitea", "api": "https://git.example/api/v1/repos/x/fj/releases/latest" }""")))!;
        Assert.Equal("v3.0", r.Tag);
        Assert.Equal(new DateTime(2026, 9, 30, 22, 0, 0, DateTimeKind.Utc), r.Date);
        Assert.Null(Assert.Single(http.Requests).Headers.Authorization);
        Assert.StartsWith("Wintainium/", http.Requests[0].Headers.UserAgent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitLab_source_resolves()
    {
        const string api = "https://gitlab.com/api/v4/projects/x%2Flab/releases/permalink/latest";
        var http = new FakeHttp().OnJson(api,
            """
                { "tag_name": "v9.9", "released_at": "2026-11-01T00:00:00Z", "_links": { "self": "https://gitlab.com/x/lab/-/releases/v9.9" },
                                 "assets": { "links": [ { "name": "lab-win.zip", "url": "https://gitlab.com/x/lab/-/package_files/1/download" } ] }, "description": "d" }
                """);
        using HttpClient client = http.Client();
        ReleaseInfo r = (await new ReleaseSources(client).GetLatestAsync(App($$"""{ "source": "gitlab", "api": "{{api}}" }""")))!;
        Assert.Equal("v9.9", r.Tag);
        Assert.Equal("https://gitlab.com/x/lab/-/releases/v9.9", r.Page);
        Assert.Equal(new ReleaseAsset("lab-win.zip", "https://gitlab.com/x/lab/-/package_files/1/download"), Assert.Single(r.Assets));
        Assert.Equal("d", r.Notes);
    }

    [Fact]
    public async Task Dolphin_feed_resolves()
    {
        const string api = "https://dolphin-emu.org/update/latest/beta/";
        var http = new FakeHttp().OnJson(api,
            """
                { "shortrev": "2609b", "date": "2026-11-01T00:00:00Z", "changelog_html": "<p>Fix &amp; &quot;thing&quot; isn&#39;t</p>",
                                 "artifacts": [ { "system": "Windows x64", "url": "https://dl.dolphin-emu.org/builds/dolphin-2609b-x64.7z" },
                                                { "system": "macOS", "url": "https://dl.dolphin-emu.org/builds/dolphin-2609b.dmg" } ] }
                """);
        using HttpClient client = http.Client();
        ReleaseInfo r = (await new ReleaseSources(client).GetLatestAsync(App($$"""{ "source": "dolphin", "api": "{{api}}" }""")))!;
        Assert.Equal("2609b", r.Tag);
        Assert.Equal("https://dolphin-emu.org/download/", r.Page);
        Assert.Equal("dolphin-2609b-x64.7z", Assert.Single(r.Assets).Name);
        Assert.Equal("Fix & \"thing\" isn't", r.Notes);
    }

    [Fact]
    public async Task Dolphin_notes_decode_every_entity_once()
    {
        const string api = "https://dolphin-emu.org/update/latest/beta/";
        var http = new FakeHttp().OnJson(api, """{ "shortrev": "1", "changelog_html": "<p>a &lt;b&gt; &amp;lt; c</p>" }""");
        using HttpClient client = http.Client();
        ReleaseInfo r = (await new ReleaseSources(client).GetLatestAsync(App($$"""{ "source": "dolphin", "api": "{{api}}" }""")))!;
        Assert.Equal("a <b> &lt; c", r.Notes);
    }

    [Fact]
    public async Task GitHub_tag_and_repo_are_escaped_in_the_address()
    {
        var http = new FakeHttp().OnJson("https://api.github.com/repos/o/ok/releases/tags/v1%231%3Fx%2Fy",
            """{ "tag_name": "v1#1?x/y", "html_url": "https://x", "assets": [] }""");
        using HttpClient client = http.Client();
        ReleaseInfo r = (await new ReleaseSources(client).GetLatestAsync(App("""{ "source": "github", "repo": "o/ok", "tag": "v1#1?x/y" }""")))!;
        Assert.Equal("v1#1?x/y", r.Tag);
        Assert.Single(http.Requests);
    }

    [Theory]
    [InlineData("justaname")]
    [InlineData("o/n/extra")]
    [InlineData("o/..")]
    [InlineData("/n")]
    public async Task GitHub_repo_must_be_owner_slash_name(string repo)
    {
        var http = new FakeHttp();
        using HttpClient client = http.Client();
        await Assert.ThrowsAsync<LookupException>(() => new ReleaseSources(client).GetLatestAsync(App($$"""{ "source": "github", "repo": "{{repo}}" }""")));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Manual_source_is_not_looked_up()
    {
        var http = new FakeHttp();
        using HttpClient client = http.Client();
        Assert.Null(await new ReleaseSources(client).GetLatestAsync(App("""{ "source": "manual", "url": "https://x" }""")));
        Assert.Empty(http.Requests);
    }

    [Fact(DisplayName = "itch.io request looks like a browser and identifies Wintainium")]
    public async Task Itch_request_looks_like_a_browser()
    {
        var http = new FakeHttp().On("https://dev.itch.io/fresh", _ => FakeHttp.Text("""<strong class="name">g.zip</strong><span class="icon-windows"></span>"""));
        using HttpClient client = http.Client();
        await new ReleaseSources(client, "ghp_secret").GetLatestAsync(App("""{ "source": "itch", "url": "https://dev.itch.io/fresh" }"""));
        HttpRequestMessage req = Assert.Single(http.Requests);
        Assert.Matches("^Mozilla/5\\.0 .*Wintainium/", req.Headers.UserAgent.ToString());
        Assert.Contains("text/html", req.Headers.Accept.ToString(), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "itch.io request never carries the GitHub token")]
    public async Task Itch_request_never_carries_the_token()
    {
        var http = new FakeHttp().On("https://dev.itch.io/fresh", _ => FakeHttp.Text("""<strong class="name">g.zip</strong>"""));
        using HttpClient client = http.Client();
        await new ReleaseSources(client, "ghp_secret").GetLatestAsync(App("""{ "source": "itch", "url": "https://dev.itch.io/fresh" }"""));
        Assert.Null(Assert.Single(http.Requests).Headers.Authorization);
    }

    [Fact]
    public async Task GitHub_token_goes_only_to_api_github_com()
    {
        var http = new FakeHttp().OnJson("https://api.github.com/repos/o/ok/releases/latest", """{ "tag_name": "v1" }""");
        using HttpClient client = http.Client();
        await new ReleaseSources(client, "ghp_secret").GetLatestAsync(App("""{ "source": "github", "repo": "o/ok" }"""));
        Assert.Equal("Bearer ghp_secret", Assert.Single(http.Requests).Headers.Authorization!.ToString());
    }

    [Fact(DisplayName = "http release address is not requested")]
    public async Task Http_address_is_not_requested()
    {
        var http = new FakeHttp();
        using HttpClient client = http.Client();
        var sources = new ReleaseSources(client);
        LookupException ex = await Assert.ThrowsAsync<LookupException>(() =>
            sources.GetLatestAsync(App("""{ "source": "gitea", "api": "http://git.example/api/v1/repos/x/y/releases/latest" }""")));
        Assert.Contains("is not an https link", ex.Message, StringComparison.Ordinal);
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData("""{ "source": "github" }""", "GitHub")]
    [InlineData("""{ "source": "itch" }""", "itch.io")]
    [InlineData("""{ "source": "gitlab" }""", "GitLab")]
    [InlineData("""{ "source": "dolphin" }""", "dolphin-emu.org")]
    [InlineData("""{ "source": "gitea", "api": "https://codeberg.org/api/v1/repos/x/y/releases/latest" }""", "codeberg.org")]
    [InlineData("""{ "source": "manual" }""", "the release site")]
    public void Site_names(string json, string site) => Assert.Equal(site, ReleaseSources.SiteName(App(json)));

    [Fact]
    public async Task Server_errors_carry_the_status()
    {
        var http = new FakeHttp().OnStatus("https://api.github.com/repos/o/limited/releases/latest", HttpStatusCode.Forbidden);
        using HttpClient client = http.Client();
        LookupException ex = await Assert.ThrowsAsync<LookupException>(() =>
            new ReleaseSources(client).GetLatestAsync(App("""{ "source": "github", "repo": "o/limited" }""")));
        Assert.Equal(HttpStatusCode.Forbidden, ex.Status);
    }

    [Fact]
    public async Task A_slow_site_times_out_as_unreachable()
    {
        var http = new FakeHttp().On("https://dev.itch.io/slow", _ => throw new TaskCanceledException("timeout"));
        using HttpClient client = http.Client();
        LookupException ex = await Assert.ThrowsAsync<LookupException>(() =>
            new ReleaseSources(client).GetLatestAsync(App("""{ "source": "itch", "url": "https://dev.itch.io/slow" }""")));
        Assert.True(ex.Unreachable);
        Assert.Contains("timed out", ex.Message, StringComparison.Ordinal);
    }

    internal static string IsoOf(DateTime d) => Iso(d);
}
