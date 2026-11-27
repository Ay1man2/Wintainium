using Wintainium.Core.Data;
using Wintainium.Core.Releases;

namespace Wintainium.Core.Status;

/// <summary>One row of the grid: an app and what is known about it now.</summary>
public sealed class AppRow
{
    public required AppEntry Entry { get; init; }

    public string App => Entry.Name;

    public required string Status { get; init; }

    public required string Installed { get; init; }

    /// <summary>Latest tag, with " (cached)" when the source failed and its saved answer is shown.</summary>
    public required string Latest { get; init; }

    /// <summary>The latest tag alone.</summary>
    public string LatestTag { get; init; } = "";

    public StaleInfo? Stale { get; init; }

    /// <summary>Release date, local yyyy-MM-dd.</summary>
    public required string Released { get; init; }

    public required IReadOnlyList<string> Methods { get; init; }

    /// <summary>Label of the first update method ("Update via" column).</summary>
    public string Via => StatusEngine.MethodLabel(Methods[0]);

    public required string Page { get; init; }

    /// <summary>The program path with %VARS% expanded.</summary>
    public required string Exe { get; init; }

    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];

    public string Notes { get; init; } = "";

    /// <summary>Wintainium's own entry.</summary>
    public bool Self => Entry.IsSelf;

    /// <summary>Shown in gold (own entry, unless turned off).</summary>
    public bool Gold { get; set; }
}
