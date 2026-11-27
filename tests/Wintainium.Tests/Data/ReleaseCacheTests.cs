using Wintainium.Core.Data;
using Wintainium.Core.Releases;
using Wintainium.Tests.Helpers;

namespace Wintainium.Tests.Data;

public class ReleaseCacheTests
{
    [Fact(DisplayName = "cache file written")]
    public void Cache_round_trips()
    {
        using var dir = new TempDir();
        string path = dir.File(DataFolder.CacheFileName);
        ReleaseCache cache = ReleaseCache.Load(path);
        var date = new DateTime(2026, 11, 20, 10, 30, 0, DateTimeKind.Utc);
        var fetched = new DateTime(2026, 11, 27, 1, 0, 0, DateTimeKind.Utc);
        cache.Set("PCSX2", new ReleaseInfo
        {
            Tag = "v2.9.181",
            Date = date,
            Page = "https://github.com/PCSX2/pcsx2/releases/tag/v2.9.181",
            Assets = [new ReleaseAsset("pcsx2-v2.9.181-windows-x64.7z", "https://github.com/PCSX2/pcsx2/releases/download/v2.9.181/pcsx2.7z")],
            Notes = "notes",
        }, fetched);
        cache.Save();
        Assert.True(File.Exists(path));

        CachedRelease? c = ReleaseCache.Load(path).Get("pcsx2");
        Assert.NotNull(c);
        Assert.Equal("v2.9.181", c.Release.Tag);
        Assert.Equal(date, c.Release.Date);
        Assert.Equal(fetched, c.FetchedUtc);
        Assert.Equal("pcsx2-v2.9.181-windows-x64.7z", Assert.Single(c.Release.Assets).Name);
        Assert.Equal("notes", c.Release.Notes);
    }

    [Fact]
    public void Reads_what_PowerShell_wrote()
    {
        using var dir = new TempDir();
        // PowerShell 7 writes dates as ISO strings; Windows PowerShell 5.1 re-saves dates it parsed
        // as \/Date(ms)\/ and a one-item list as a plain object.
        string path = dir.Write(DataFolder.CacheFileName, """
            {
              "A": { "Tag": "1.0", "Date": "2026-11-20T10:30:00.0000000Z", "Page": "https://a", "Assets": [], "Notes": "", "Fetched": "2026-11-27T01:00:00.0000000Z" },
              "B": { "Tag": "2.0", "Date": "\/Date(1763634600000)\/", "Page": "https://b",
                     "Assets": { "name": "b.zip", "browser_download_url": "https://b/b.zip" }, "Notes": null, "Fetched": "2026-11-27T01:00:00Z" }
            }
            """);
        ReleaseCache cache = ReleaseCache.Load(path);
        Assert.Equal(new DateTime(2026, 11, 20, 10, 30, 0, DateTimeKind.Utc), cache.Get("A")!.Release.Date);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1763634600000).UtcDateTime, cache.Get("B")!.Release.Date);
        Assert.Equal("b.zip", Assert.Single(cache.Get("B")!.Release.Assets).Name);
        Assert.Equal("", cache.Get("B")!.Release.Notes);
    }

    [Fact]
    public void Cutting_notes_does_not_split_an_emoji()
    {
        using var dir = new TempDir();
        ReleaseCache cache = ReleaseCache.Load(dir.File("c.json"));
        string notes = new string('n', ReleaseCache.MaxNotes - 1) + "\U0001F600" + "tail";
        cache.Set("X", new ReleaseInfo { Tag = "1", Notes = notes }, DateTime.UtcNow);
        string cut = cache.Get("X")!.Release.Notes;
        Assert.Equal(ReleaseCache.MaxNotes - 1, cut.Length);
        Assert.False(char.IsHighSurrogate(cut[^1]));
    }

    [Fact]
    public void Long_notes_are_cut()
    {
        using var dir = new TempDir();
        ReleaseCache cache = ReleaseCache.Load(dir.File("c.json"));
        cache.Set("X", new ReleaseInfo { Tag = "1", Notes = new string('n', 25000) }, DateTime.UtcNow);
        Assert.Equal(ReleaseCache.MaxNotes, cache.Get("X")!.Release.Notes.Length);
    }

    [Fact]
    public void Unreadable_cache_counts_as_empty()
    {
        using var dir = new TempDir();
        Assert.Equal(0, ReleaseCache.Load(dir.Write("c.json", "{ broken")).Count);
    }
}
