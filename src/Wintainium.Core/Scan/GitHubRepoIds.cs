using System.Text.Json.Nodes;
using Wintainium.Core.Json;
using Wintainium.Core.Releases;

namespace Wintainium.Core.Scan;

/// <summary>
/// Looks repository IDs up at api.github.com/repositories/{id}, once each. The token, when
/// there is one, is only ever sent there (P-10).
/// </summary>
public sealed class GitHubRepoIds(HttpClient http, string? token) : IRepoIdResolver
{
    private readonly Dictionary<string, string> cache = new(StringComparer.Ordinal);

    public string Resolve(string id)
    {
        lock (cache)
        {
            if (cache.TryGetValue(id, out string? known))
            {
                return known;
            }
        }

        string name = "";
        if (id.All(char.IsAsciiDigit) && id.Length > 0)
        {
            try
            {
                using var cts = new CancellationTokenSource(ReleaseSources.DefaultTimeout);
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repositories/{id}");
                req.Headers.UserAgent.ParseAdd("Wintainium");
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                if (!string.IsNullOrEmpty(token))
                {
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }

                using HttpResponseMessage resp = http.Send(req, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    using var reader = new StreamReader(resp.Content.ReadAsStream(cts.Token));
                    if (JsonNode.Parse(reader.ReadToEnd()) is JsonObject o)
                    {
                        name = o.GetString("full_name");
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            {
                name = "";
            }
        }

        lock (cache)
        {
            cache[id] = name;
        }

        return name;
    }
}
