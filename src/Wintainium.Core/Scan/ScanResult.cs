using System.Text.Json.Nodes;

namespace Wintainium.Core.Scan;

public enum ScanKind
{
    Known,
    Download,
    App,
    Mod,
}

/// <summary>One thing the scan found.</summary>
public sealed record ScanRow(ScanKind Kind, string Name, string Source, string Repo, bool Tracked, string Path, string Detail, string Url = "", JsonObject? Def = null);

public enum CandidateKind
{
    /// <summary>Recognized at its usual install path: a complete entry.</summary>
    Known,

    /// <summary>A program whose files came from a code host or itch.io page.</summary>
    Found,

    /// <summary>A download with no installed program found: needs Locate.</summary>
    Download,
}

/// <summary>A row of the Scan window's checklist.</summary>
public sealed record ScanCandidate(CandidateKind Kind, string Name, string From, string Exe, JsonObject Def, bool Selected, string Note, string Key);

/// <summary>What the Scan window shows.</summary>
public sealed record ScanResult(IReadOnlyList<ScanCandidate> Candidates, int Hidden, int ModCount, IReadOnlyList<ScanRow> Rows, IReadOnlyList<string> Roots);
