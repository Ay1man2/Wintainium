using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Wintainium.Core.Data;
using Wintainium.Core.Json;

namespace Wintainium.Core.Releases;

/// <summary>
/// Asks an app's release source for its latest release (Get-LatestRelease): GitHub, GitLab,
/// Gitea/Forgejo/Codeberg, itch.io, the Dolphin feed. Every request has a timeout. The GitHub
/// token is only ever sent to api.github.com.
/// </summary>
public sealed partial class ReleaseSources
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ItchTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient http;

    public ReleaseSources(HttpClient http, string? gitHubToken = null)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        GitHubToken = gitHubToken;
    }

    /// <summary>Token for api.github.com, or null for anonymous lookups (60 an hour).</summary>
    public string? GitHubToken { get; set; }

    public static string UserAgent => $"Wintainium/{AppInfo.Version}";

    /// <summary>itch.io serves web pages, not an API, and is quicker to refuse requests that don't look like a browser.</summary>
    public static string ItchUserAgent => $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) Wintainium/{AppInfo.Version} (+https://github.com/Ay1man2/Wintainium)";

    /// <summary>The address a non-GitHub app is looked up at (GitHub's is built in), or null (Get-LookupUrl).</summary>
    public static string? LookupUrl(AppEntry app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Source.ToLowerInvariant() switch
        {
            "gitea" or "gitlab" or "dolphin" => app.Api,
            "itch" => app.Url,
            _ => null,
        };
    }

    /// <summary>The site's name for messages (Get-SourceSite).</summary>
    public static string SiteName(AppEntry app)
    {
        ArgumentNullException.ThrowIfNull(app);
        switch (app.Source.ToLowerInvariant())
        {
            case "github": return "GitHub";
            case "itch": return "itch.io";
            case "gitlab": return "GitLab";
            case "dolphin": return "dolphin-emu.org";
        }

        foreach (string u in new[] { app.Api, app.Url })
        {
            Match m = HostRegex().Match(u);
            if (m.Success)
            {
                return m.Groups[1].Value;
            }
        }

        return "the release site";
    }

    /// <summary>The latest release, or null for a source Wintainium doesn't look up. Throws <see cref="LookupException"/>.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(AppEntry app, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        string? lookup = LookupUrl(app);
        if (lookup is not null && !Links.IsHttps(lookup))
        {
            throw new LookupException($"{lookup} is not an https link");
        }

        switch (app.Source.ToLowerInvariant())
        {
            case "github":
                return await GitHubAsync(app, cancel).ConfigureAwait(false);
            case "gitea":
                return FromGiteaOrGitHub(await GetJsonAsync(lookup!, cancel).ConfigureAwait(false), useAssetDates: false);
            case "gitlab":
                return FromGitLab(await GetJsonAsync(lookup!, cancel).ConfigureAwait(false));
            case "itch":
                return ItchPage.Read(await GetItchPageAsync(lookup!, cancel).ConfigureAwait(false), lookup!, app.Asset);
            case "dolphin":
                return FromDolphin(await GetJsonAsync(lookup!, cancel).ConfigureAwait(false));
            default:
                return null;
        }
    }

    private async Task<ReleaseInfo> GitHubAsync(AppEntry app, CancellationToken cancel)
    {
        string[] parts = app.Repo.Split('/');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0 || parts.Any(p => p is "." or ".."))
        {
            throw new LookupException($"repo must look like owner/name, not \"{app.Repo}\"");
        }

        string baseUrl = $"https://api.github.com/repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}";
        if (app.Tag.Length > 0)
        {
            JsonNode? r = await GetJsonAsync($"{baseUrl}/releases/tags/{Uri.EscapeDataString(app.Tag)}", cancel, gitHub: true).ConfigureAwait(false);
            return FromGiteaOrGitHub(r, useAssetDates: true);
        }

        if (app.Prerelease)
        {
            JsonNode? list = await GetJsonAsync($"{baseUrl}/releases?per_page=10", cancel, gitHub: true).ConfigureAwait(false);
            JsonNode? first = (list as JsonArray)?.FirstOrDefault(x => x is JsonObject o && !o.IsTruthy("draft"));
            return FromGiteaOrGitHub(first, useAssetDates: false);
        }

        return FromGiteaOrGitHub(await GetJsonAsync($"{baseUrl}/releases/latest", cancel, gitHub: true).ConfigureAwait(false), useAssetDates: false);
    }

    private static ReleaseInfo FromGiteaOrGitHub(JsonNode? node, bool useAssetDates)
    {
        if (node is not JsonObject r)
        {
            throw new LookupException("no release found");
        }

        var assets = new List<ReleaseAsset>();
        DateTime? newestAsset = null;
        if (r.GetField("assets") is JsonArray arr)
        {
            foreach (JsonNode? a in arr)
            {
                if (a is JsonObject ao)
                {
                    assets.Add(new ReleaseAsset(ao.GetString("name"), ao.GetString("browser_download_url")));
                    DateTime? updated = JsonFiles.ReadUtcDate(ao.GetField("updated_at"));
                    if (updated is not null && (newestAsset is null || updated > newestAsset))
                    {
                        newestAsset = updated;
                    }
                }
            }
        }

        // A rolling tag (nightly, continuous) keeps its publish date; its files' dates say when it last changed.
        DateTime? date = useAssetDates && newestAsset is not null ? newestAsset : JsonFiles.ReadUtcDate(r.GetField("published_at"));
        return new ReleaseInfo
        {
            Tag = r.GetString("tag_name"),
            Date = date,
            Page = r.GetString("html_url"),
            Assets = assets,
            Notes = r.GetString("body"),
        };
    }

    private static ReleaseInfo FromGitLab(JsonNode? node)
    {
        if (node is not JsonObject g)
        {
            throw new LookupException("no release found");
        }

        var assets = new List<ReleaseAsset>();
        if ((g.GetField("assets") as JsonObject)?.GetField("links") is JsonArray links)
        {
            foreach (JsonNode? l in links)
            {
                if (l is JsonObject lo)
                {
                    assets.Add(new ReleaseAsset(lo.GetString("name"), lo.GetString("url")));
                }
            }
        }

        return new ReleaseInfo
        {
            Tag = g.GetString("tag_name"),
            Date = JsonFiles.ReadUtcDate(g.GetField("released_at")),
            Page = (g.GetField("_links") as JsonObject)?.GetString("self") ?? "",
            Assets = assets,
            Notes = g.GetString("description"),
        };
    }

    private static ReleaseInfo FromDolphin(JsonNode? node)
    {
        if (node is not JsonObject d)
        {
            throw new LookupException("no release found");
        }

        var assets = new List<ReleaseAsset>();
        if (d.GetField("artifacts") is JsonArray arr)
        {
            foreach (JsonNode? a in arr)
            {
                if (a is JsonObject ao && string.Equals(ao.GetString("system"), "Windows x64", StringComparison.OrdinalIgnoreCase))
                {
                    string url = ao.GetString("url");
                    assets.Add(new ReleaseAsset(FileNameOf(url), url));
                }
            }
        }

        // v0.3.0 decoded only &amp; &quot; &#39; (and &amp; first); decode every entity once
        string notes = System.Net.WebUtility.HtmlDecode(TagRegex().Replace(d.GetString("changelog_html"), "")).Trim();
        return new ReleaseInfo
        {
            Tag = d.GetString("shortrev"),
            Date = JsonFiles.ReadUtcDate(d.GetField("date")),
            Page = "https://dolphin-emu.org/download/",
            Assets = assets,
            Notes = notes,
        };
    }

    /// <summary>[IO.Path]::GetFileName on a URL: the text after the last / or \.</summary>
    public static string FileNameOf(string url)
    {
        int cut = url.LastIndexOfAny(['/', '\\']);
        return cut >= 0 ? url[(cut + 1)..] : url;
    }

    private async Task<JsonNode?> GetJsonAsync(string url, CancellationToken cancel, bool gitHub = false)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (gitHub)
        {
            req.Headers.UserAgent.ParseAdd("Wintainium");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            if (!string.IsNullOrEmpty(GitHubToken) && req.RequestUri!.Host == "api.github.com")
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GitHubToken);
            }
        }
        else
        {
            req.Headers.UserAgent.ParseAdd(UserAgent);
        }

        string body = await SendAsync(req, DefaultTimeout, cancel).ConfigureAwait(false);
        try
        {
            return JsonNode.Parse(body, documentOptions: JsonFiles.ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new LookupException($"the answer from {req.RequestUri!.Host} is not valid JSON", null, "", unreachable: false, ex);
        }
    }

    private async Task<string> GetItchPageAsync(string url, CancellationToken cancel)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", ItchUserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        return await SendAsync(req, ItchTimeout, cancel).ConfigureAwait(false);
    }

    private async Task<string> SendAsync(HttpRequestMessage req, TimeSpan timeout, CancellationToken cancel)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(timeout);
        try
        {
            using HttpResponseMessage resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                int code = (int)resp.StatusCode;
                throw new LookupException(
                    $"Response status code does not indicate success: {code} ({resp.ReasonPhrase}).",
                    resp.StatusCode,
                    body,
                    unreachable: false);
            }

            return body;
        }
        catch (OperationCanceledException ex) when (!cancel.IsCancellationRequested)
        {
            throw new LookupException($"The request to {req.RequestUri!.Host} timed out.", null, "", unreachable: true, ex);
        }
        catch (HttpRequestException ex)
        {
            HttpStatusCode? status = ex.StatusCode;
            bool unreachable = status is null && ex.HttpRequestError is HttpRequestError.NameResolutionError
                or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError
                or HttpRequestError.ResponseEnded or HttpRequestError.ProxyTunnelError;
            throw new LookupException(ex.Message, status, "", unreachable, ex);
        }
    }

    [GeneratedRegex("^https?://([^/]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HostRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();
}
