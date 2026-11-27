using Wintainium.Core.Data;

namespace Wintainium.Core.Releases;

/// <summary>Why a shown release is an older, saved answer.</summary>
public sealed record StaleInfo(string Reason, string Site, DateTime? FetchedUtc);

/// <summary>The outcome of looking up one app: a release (maybe a saved one), or a failure status.</summary>
public sealed record LookupResult
{
    public ReleaseInfo? Release { get; init; }

    /// <summary>Set when the source failed and the last saved answer is shown instead.</summary>
    public StaleInfo? Stale { get; init; }

    /// <summary>Failure status when there is no release to show (token rejected, rate limited, site busy, lookup failed).</summary>
    public string? FailureStatus { get; init; }
}

/// <summary>
/// Release lookups with the cache (Get-LatestCached): answers are reused for the cache window,
/// Re-check all asks every source again, and a source that is briefly down falls back to its
/// last answer instead of turning a known app red. Lookups run in parallel, a few at a time.
/// </summary>
public sealed class ReleaseLookup
{
    /// <summary>How many sources are asked at the same time.</summary>
    public const int MaxParallel = 6;

    private readonly ReleaseSources sources;
    private readonly ReleaseCache cache;
    private readonly Func<DateTime> utcNow;
    private readonly object gate = new();

    public ReleaseLookup(ReleaseSources sources, ReleaseCache cache, int cacheMinutes, Func<DateTime>? utcNow = null)
    {
        this.sources = sources ?? throw new ArgumentNullException(nameof(sources));
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        CacheMinutes = cacheMinutes;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public int CacheMinutes { get; }

    public async Task<LookupResult> LookupAsync(AppEntry app, bool force, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        CachedRelease? c;
        lock (gate)
        {
            c = cache.Get(app.Name);
        }

        if (!force && c?.FetchedUtc is DateTime fetched && (utcNow() - fetched).TotalMinutes < CacheMinutes)
        {
            return new LookupResult { Release = c.Release };
        }

        ReleaseInfo? release;
        try
        {
            release = await sources.GetLatestAsync(app, cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is LookupException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or ArgumentException)
        {
            string reason = LookupStatus.For(ex, app);
            if (c is not null && c.Release.Tag.Length > 0)
            {
                return new LookupResult
                {
                    Release = c.Release,
                    Stale = new StaleInfo(reason, ReleaseSources.SiteName(app), c.FetchedUtc),
                };
            }

            return new LookupResult { FailureStatus = reason };
        }

        if (release is not null)
        {
            lock (gate)
            {
                cache.Set(app.Name, release, utcNow());
                release = cache.Get(app.Name)!.Release;
            }
        }

        return new LookupResult { Release = release };
    }

    /// <summary>Looks up every app (except manual ones), a few at a time, then saves the cache.</summary>
    public async Task<IReadOnlyDictionary<AppEntry, LookupResult>> LookupAllAsync(
        IEnumerable<AppEntry> apps, bool force, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(apps);
        var results = new Dictionary<AppEntry, LookupResult>();
        using var gateParallel = new SemaphoreSlim(MaxParallel);
        var tasks = apps
            .Where(a => !string.Equals(a.Source, "manual", StringComparison.OrdinalIgnoreCase))
            .Select(async app =>
            {
                await gateParallel.WaitAsync(cancel).ConfigureAwait(false);
                try
                {
                    progress?.Report($"checking {app.Name}...");
                    LookupResult r = await LookupAsync(app, force, cancel).ConfigureAwait(false);
                    lock (results)
                    {
                        results[app] = r;
                    }
                }
                finally
                {
                    gateParallel.Release();
                }
            })
            .ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        lock (gate)
        {
            cache.Save();
        }

        return results;
    }
}
