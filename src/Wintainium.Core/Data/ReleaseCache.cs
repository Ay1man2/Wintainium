using System.Text.Json;
using System.Text.Json.Nodes;
using Wintainium.Core.Json;
using Wintainium.Core.Releases;

namespace Wintainium.Core.Data;

/// <summary>A cached lookup: the release and when it was fetched.</summary>
public sealed record CachedRelease(ReleaseInfo Release, DateTime? FetchedUtc);

/// <summary>
/// wintainium-cache.json: the last answer of each release source, keyed by app name
/// (case-insensitive, like a PowerShell hashtable). A file that can't be read counts as empty (D-08).
/// </summary>
public sealed class ReleaseCache
{
    /// <summary>Release notes are cut to this many characters before they are cached.</summary>
    public const int MaxNotes = 20000;

    private readonly Dictionary<string, CachedRelease> items = new(StringComparer.OrdinalIgnoreCase);

    private ReleaseCache(string path)
    {
        FilePath = path;
    }

    public string FilePath { get; }

    public int Count => items.Count;

    public static ReleaseCache Load(string path)
    {
        var cache = new ReleaseCache(path);
        if (!File.Exists(path))
        {
            return cache;
        }

        try
        {
            if (JsonFiles.ReadFile(path) is not JsonObject root)
            {
                return cache;
            }

            foreach (KeyValuePair<string, JsonNode?> kv in root)
            {
                if (kv.Value is JsonObject o)
                {
                    cache.items[kv.Key] = FromJson(o);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            cache.items.Clear();
        }

        return cache;
    }

    public CachedRelease? Get(string appName) => items.GetValueOrDefault(appName);

    public void Set(string appName, ReleaseInfo release, DateTime fetchedUtc)
    {
        string notes = release.Notes;
        if (notes.Length > MaxNotes)
        {
            // don't split a surrogate pair
            int cut = char.IsHighSurrogate(notes[MaxNotes - 1]) ? MaxNotes - 1 : MaxNotes;
            notes = notes[..cut];
        }

        items[appName] = new CachedRelease(release with { Notes = notes }, fetchedUtc);
    }

    public bool Remove(string appName) => items.Remove(appName);

    /// <summary>Writes the cache. Failures are ignored, as in v0.3.0: the cache is only a convenience.</summary>
    public void Save()
    {
        var root = new JsonObject();
        foreach (KeyValuePair<string, CachedRelease> kv in items)
        {
            root[kv.Key] = ToJson(kv.Value);
        }

        try
        {
            JsonFiles.WriteFileAtomic(FilePath, root, keepBackup: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not worth bothering anyone about
        }
    }

    private static CachedRelease FromJson(JsonObject o)
    {
        var assets = new List<ReleaseAsset>();
        if (o.GetField("Assets") is JsonArray arr)
        {
            foreach (JsonNode? a in arr)
            {
                if (a is JsonObject ao)
                {
                    assets.Add(new ReleaseAsset(ao.GetString("name"), ao.GetString("browser_download_url")));
                }
            }
        }
        else if (o.GetField("Assets") is JsonObject single)
        {
            // Windows PowerShell 5.1 writes a one-item list as a plain object
            assets.Add(new ReleaseAsset(single.GetString("name"), single.GetString("browser_download_url")));
        }

        var release = new ReleaseInfo
        {
            Tag = o.GetString("Tag"),
            Date = JsonFiles.ReadUtcDate(o.GetField("Date")),
            Page = o.GetString("Page"),
            Assets = assets,
            Notes = o.GetString("Notes"),
        };
        return new CachedRelease(release, JsonFiles.ReadUtcDate(o.GetField("Fetched")));
    }

    private static JsonObject ToJson(CachedRelease c) => new()
    {
        ["Tag"] = c.Release.Tag,
        ["Date"] = JsonFiles.FormatUtcDate(c.Release.Date),
        ["Page"] = c.Release.Page,
        ["Assets"] = new JsonArray([.. c.Release.Assets.Select(a => (JsonNode)new JsonObject
        {
            ["name"] = a.Name,
            ["browser_download_url"] = a.Url,
        })]),
        ["Notes"] = c.Release.Notes,
        ["Fetched"] = JsonFiles.FormatUtcDate(c.FetchedUtc),
    };
}
