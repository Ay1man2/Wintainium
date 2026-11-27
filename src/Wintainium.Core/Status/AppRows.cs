using Wintainium.Core.Data;
using Wintainium.Core.Releases;

namespace Wintainium.Core.Status;

/// <summary>Looks up every app and builds the grid's rows (Get-AppRows), with lookups in parallel.</summary>
public sealed class AppRows
{
    private readonly ReleaseLookup lookup;
    private readonly IExeProbe probe;
    private readonly StatusEngine.SelfInfo? self;

    public AppRows(ReleaseLookup lookup, IExeProbe probe, StatusEngine.SelfInfo? self)
    {
        this.lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
        this.self = self;
    }

    public async Task<IReadOnlyList<AppRow>> BuildAsync(
        IReadOnlyList<AppEntry> apps, Settings settings, bool force, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(settings);
        IReadOnlyDictionary<AppEntry, LookupResult> results = await lookup.LookupAllAsync(apps, force, progress, cancel).ConfigureAwait(false);
        var rows = new List<AppRow>(apps.Count);
        foreach (AppEntry app in apps)
        {
            ExeInfo exe = probe.Probe(app.ExpandedExe);
            rows.Add(StatusEngine.BuildRow(app, results.GetValueOrDefault(app), exe, settings.GraceDays, settings.GoldSelf, self));
        }

        return StatusEngine.Order(rows);
    }
}
