using System.Net;
using Wintainium.Core.Data;
using Wintainium.Core.Releases;
using Wintainium.Tests.Helpers;

namespace Wintainium.Tests.Releases;

public class ReleaseLookupTests
{
    private static AppEntry App(string json) => new(System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject());

    private static readonly AppEntry ItchNew = App("""{ "name": "ItchNew", "source": "itch", "url": "https://dev.itch.io/fresh" }""");
    private static readonly AppEntry ItchCached = App("""{ "name": "ItchCached", "source": "itch", "url": "https://dev.itch.io/cached" }""");
    private static readonly AppEntry ForgeDown = App("""{ "name": "ForgeDown", "source": "gitea", "api": "https://codeberg.org/api/v1/repos/x/y/releases/latest" }""");
    private static readonly AppEntry GhBadToken = App("""{ "name": "GhBadToken", "source": "github", "repo": "o/badtoken" }""");
    private static readonly AppEntry GhOk = App("""{ "name": "GhOk", "source": "github", "repo": "o/ok" }""");
    private static readonly AppEntry Broken = App("""{ "name": "Broken", "source": "github", "repo": "o/broken" }""");
    private static readonly AppEntry Limited = App("""{ "name": "Limited", "source": "github", "repo": "o/limited" }""");
    private static readonly AppEntry HttpForge = App("""{ "name": "HttpForge", "source": "gitea", "api": "http://git.example/api/v1/repos/x/y/releases/latest" }""");

    private static FakeHttp Network() => new FakeHttp()
        .OnStatus("https://dev.itch.io/fresh", HttpStatusCode.Forbidden)
        .OnStatus("https://dev.itch.io/cached", HttpStatusCode.Forbidden)
        .OnStatus("https://codeberg.org/api/v1/repos/x/y/releases/latest", HttpStatusCode.ServiceUnavailable)
        .OnJson("https://api.github.com/repos/o/badtoken/releases/latest", """{"message":"Bad credentials"}""", HttpStatusCode.Unauthorized)
        .OnJson("https://api.github.com/repos/o/ok/releases/latest", """{ "tag_name": "v1.0", "published_at": "2026-10-01T00:00:00Z", "html_url": "https://gh/ok", "assets": [] }""")
        .OnJson("https://api.github.com/repos/o/limited/releases/latest", """{"message":"API rate limit exceeded"}""", HttpStatusCode.Forbidden);

    private static (ReleaseLookup Lookup, ReleaseCache Cache, TempDir Dir) Make(HttpClient client, DateTime now)
    {
        var dir = new TempDir();
        string cachePath = dir.Write(DataFolder.CacheFileName, $$"""
            { "ItchCached": { "Tag": "2.1.0", "Date": "2026-10-01T00:00:00Z", "Page": "https://dev.itch.io/cached", "Assets": [], "Notes": "",
                              "Fetched": "{{now.AddHours(-5).ToString("o", System.Globalization.CultureInfo.InvariantCulture)}}" } }

            """);
        ReleaseCache cache = ReleaseCache.Load(cachePath);
        return (new ReleaseLookup(new ReleaseSources(client), cache, 60, () => now), cache, dir);
    }

    private static readonly DateTime Now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact(DisplayName = "itch.io 403 without a saved answer is \"site busy\", not a GitHub status")]
    public async Task Itch_403_is_site_busy()
    {
        using HttpClient client = Network().Client();
        (ReleaseLookup lookup, _, TempDir dir) = Make(client, Now);
        using (dir)
        {
            Assert.Equal(Statuses.SiteBusy, (await lookup.LookupAsync(ItchNew, force: false)).FailureStatus);
        }
    }

    [Fact(DisplayName = "itch.io 403 with a saved answer falls back to it")]
    public async Task Itch_403_falls_back_to_the_saved_answer()
    {
        using HttpClient client = Network().Client();
        (ReleaseLookup lookup, _, TempDir dir) = Make(client, Now);
        using (dir)
        {
            LookupResult r = await lookup.LookupAsync(ItchCached, force: false);
            Assert.Equal("2.1.0", r.Release!.Tag);
            Assert.Equal(Statuses.SiteBusy, r.Stale!.Reason);
            Assert.Equal("itch.io", r.Stale.Site);
            Assert.Equal(Now.AddHours(-5), r.Stale.FetchedUtc);
        }
    }

    [Fact(DisplayName = "Codeberg 503 is \"site busy\"")]
    public async Task Codeberg_503_is_site_busy()
    {
        using HttpClient client = Network().Client();
        (ReleaseLookup lookup, _, TempDir dir) = Make(client, Now);
        using (dir)
        {
            Assert.Equal(Statuses.SiteBusy, (await lookup.LookupAsync(ForgeDown, force: false)).FailureStatus);
        }
    }

    [Fact(DisplayName = "GitHub 401 is \"token rejected\"")]
    public async Task GitHub_401_is_token_rejected()
    {
        using HttpClient client = Network().Client();
        (ReleaseLookup lookup, _, TempDir dir) = Make(client, Now);
        using (dir)
        {
            Assert.Equal(Statuses.TokenRejected, (await lookup.LookupAsync(GhBadToken, force: false)).FailureStatus);
        }
    }

    [Fact(DisplayName = "GitHub lookups still work")]
    public async Task GitHub_lookups_still_work()
    {
        using HttpClient client = Network().Client();
        (ReleaseLookup lookup, _, TempDir dir) = Make(client, Now);
        using (dir)
        {
            LookupResult r = await lookup.LookupAsync(GhOk, force: false);
            Assert.Equal("v1.0", r.Release!.Tag);
            Assert.Null(r.Stale);
        }
    }

    [Fact]
    public async Task GitHub_403_is_rate_limited_and_404_lookup_failed()
    {
        using HttpClient client = Network().Client();
        (ReleaseLookup lookup, _, TempDir dir) = Make(client, Now);
        using (dir)
        {
            Assert.Equal(Statuses.RateLimited, (await lookup.LookupAsync(Limited, force: false)).FailureStatus);
            Assert.Equal(Statuses.LookupFailed, (await lookup.LookupAsync(Broken, force: false)).FailureStatus);
        }
    }

    [Fact(DisplayName = "http release address shows \"lookup failed\"")]
    public async Task Http_address_is_lookup_failed()
    {
        var http = Network();
        using HttpClient client = http.Client();
        (ReleaseLookup lookup, _, TempDir dir) = Make(client, Now);
        using (dir)
        {
            Assert.Equal(Statuses.LookupFailed, (await lookup.LookupAsync(HttpForge, force: false)).FailureStatus);
            Assert.DoesNotContain(http.Requests, r => r.RequestUri!.Scheme == "http");
        }
    }

    [Fact]
    public async Task Unreachable_sites_are_busy_but_GitHub_is_lookup_failed()
    {
        var http = new FakeHttp()
            .OnThrow("https://codeberg.org/api/v1/repos/x/y/releases/latest", new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known."))
            .OnThrow("https://api.github.com/repos/o/ok/releases/latest", new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused"));
        using HttpClient client = http.Client();
        (ReleaseLookup lookup, _, TempDir dir) = Make(client, Now);
        using (dir)
        {
            Assert.Equal(Statuses.SiteBusy, (await lookup.LookupAsync(ForgeDown, force: false)).FailureStatus);
            Assert.Equal(Statuses.LookupFailed, (await lookup.LookupAsync(GhOk, force: false)).FailureStatus);
        }
    }

    [Fact(DisplayName = "second run served from cache (only failed ones retried)")]
    public async Task Second_run_served_from_cache()
    {
        var http = Network();
        using HttpClient client = http.Client();
        (ReleaseLookup lookup, ReleaseCache cache, TempDir dir) = Make(client, Now);
        using (dir)
        {
            AppEntry[] apps = [GhOk, ForgeDown, ItchCached];
            await lookup.LookupAllAsync(apps, force: false);
            int first = http.Requests.Count;
            Assert.Equal(3, first);
            Assert.True(File.Exists(cache.FilePath));

            await lookup.LookupAllAsync(apps, force: false);
            // GhOk is cached now; ForgeDown failed and ItchCached's saved answer is old, so both are asked again
            Assert.Equal(first + 2, http.Requests.Count);
            Assert.Equal(1, http.CountFor("https://api.github.com/"));

            await lookup.LookupAllAsync(apps, force: true);
            Assert.Equal(2, http.CountFor("https://api.github.com/"));
        }
    }

    [Fact]
    public async Task Cache_is_reused_only_inside_the_window()
    {
        var http = Network();
        using HttpClient client = http.Client();
        DateTime now = Now;
        using var dir = new TempDir();
        ReleaseCache cache = ReleaseCache.Load(dir.File("c.json"));
        var lookup = new ReleaseLookup(new ReleaseSources(client), cache, 60, () => now);
        await lookup.LookupAsync(GhOk, false);
        now = now.AddMinutes(59);
        await lookup.LookupAsync(GhOk, false);
        Assert.Single(http.Requests);
        now = now.AddMinutes(2);
        await lookup.LookupAsync(GhOk, false);
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task One_unexpected_failure_does_not_lose_the_others_or_the_cache()
    {
        var http = Network().On("https://api.github.com/repos/o/broken/releases/latest", _ => throw new InvalidOperationException("odd charset"));
        using HttpClient client = http.Client();
        (ReleaseLookup lookup, ReleaseCache cache, TempDir dir) = Make(client, Now);
        using (dir)
        {
            IReadOnlyDictionary<AppEntry, LookupResult> all = await lookup.LookupAllAsync([Broken, GhOk], false);
            Assert.Equal(Statuses.LookupFailed, all[Broken].FailureStatus);
            Assert.Equal("v1.0", all[GhOk].Release!.Tag);
            Assert.Equal("v1.0", ReleaseCache.Load(cache.FilePath).Get("GhOk")!.Release.Tag);
        }
    }

    [Fact]
    public async Task Cancelling_still_saves_what_was_found()
    {
        var http = Network();
        using HttpClient client = http.Client();
        (ReleaseLookup lookup, ReleaseCache cache, TempDir dir) = Make(client, Now);
        using (dir)
        {
            await lookup.LookupAsync(GhOk, false);
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup.LookupAllAsync([GhOk, Limited], true, cancel: cts.Token));
            Assert.NotNull(ReleaseCache.Load(cache.FilePath).Get("GhOk"));
        }
    }

    [Fact]
    public async Task A_fetch_time_in_the_future_counts_as_stale()
    {
        var http = Network();
        using HttpClient client = http.Client();
        DateTime now = Now;
        using var dir = new TempDir();
        var lookup = new ReleaseLookup(new ReleaseSources(client), ReleaseCache.Load(dir.File("c.json")), 60, () => now);
        await lookup.LookupAsync(GhOk, false);
        now = now.AddHours(-3); // the clock went back
        await lookup.LookupAsync(GhOk, false);
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task Manual_apps_are_not_looked_up()
    {
        var http = Network();
        using HttpClient client = http.Client();
        using var dir = new TempDir();
        var lookup = new ReleaseLookup(new ReleaseSources(client), ReleaseCache.Load(dir.File("c.json")), 60);
        IReadOnlyDictionary<AppEntry, LookupResult> all = await lookup.LookupAllAsync([App("""{ "name": "M", "source": "manual" }""")], false);
        Assert.Empty(all);
        Assert.Empty(http.Requests);
    }
}
