namespace Wintainium.Core.Releases;

/// <summary>A file attached to a release (browser_download_url in GitHub's API).</summary>
public sealed record ReleaseAsset(string Name, string Url);

/// <summary>The latest release of an app as a source reported it.</summary>
public sealed record ReleaseInfo
{
    public string Tag { get; init; } = "";

    /// <summary>Release date in UTC, when known.</summary>
    public DateTime? Date { get; init; }

    public string Page { get; init; } = "";

    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];

    public string Notes { get; init; } = "";
}
