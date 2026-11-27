using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Wintainium.Core.Logging;
using Wintainium.Core.Releases;

namespace Wintainium.Core.Install;

/// <summary>The SHA-256 a release publishes for a file, or why it couldn't be used.</summary>
public sealed record PublishedHash(string From, string? Hash, string? Error);

public enum ChecksumOutcome
{
    Ok,

    /// <summary>Mismatch, unusable checksum file or unreadable download: the caller deletes it.</summary>
    Bad,

    /// <summary>Windows Security took the file meanwhile.</summary>
    Removed,
}

/// <summary>Published checksums (Get-PublishedHash, Test-Checksum).</summary>
public static partial class Checksums
{
    /// <summary>Finds file.sha256, else a SHA256SUMS / checksums.txt style list. Null when there is none.</summary>
    public static async Task<PublishedHash?> FindAsync(IReadOnlyList<ReleaseAsset> assets, string name, Downloader downloader, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(downloader);
        var all = assets.Where(a => a.Url.Length > 0).ToList();
        ReleaseAsset? sum = all.FirstOrDefault(a => string.Equals(a.Name, name + ".sha256", StringComparison.OrdinalIgnoreCase));
        bool perFile = sum is not null;
        sum ??= all.FirstOrDefault(a => ListName().IsMatch(a.Name));
        if (sum is null)
        {
            return null;
        }

        if (!Links.IsHttps(sum.Url))
        {
            return new PublishedHash(sum.Name, null, $"{sum.Name} is not an https link");
        }

        string? text = await downloader.GetTextAsync(sum.Url, cancel).ConfigureAwait(false);
        if (text is null)
        {
            return new PublishedHash(sum.Name, null, $"Could not read {sum.Name}");
        }

        string? line = perFile ? text : text.Split('\n').FirstOrDefault(l => l.Contains(name, StringComparison.OrdinalIgnoreCase));
        Match m = line is null ? Match.Empty : Sha256().Match(line);
        return m.Success
            ? new PublishedHash(sum.Name, m.Groups[1].Value.ToLowerInvariant(), null)
            : new PublishedHash(sum.Name, null, $"{sum.Name} does not list this file");
    }

    /// <summary>
    /// Compares a download with its published SHA-256. A checksum file that exists but can't be
    /// used refuses the download (X-03): v0.3.0 went ahead "not verified".
    /// </summary>
    public static async Task<ChecksumOutcome> VerifyAsync(IReadOnlyList<ReleaseAsset> assets, string name, string file, InstallContext ctx, Downloader downloader, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ILog log = ctx.Log;
        PublishedHash? p = await FindAsync(assets, name, downloader, cancel).ConfigureAwait(false);
        if (p is null)
        {
            log.Write("  No checksum is published for this file, so there was no integrity check.", LogColor.DarkGray);
            return ChecksumOutcome.Ok;
        }

        if (p.Error is not null)
        {
            log.Write($"  {p.Error}, so the integrity check could not be done. Not using the download.", LogColor.Red);
            return ChecksumOutcome.Bad;
        }

        string? actual = null;
        try
        {
            await using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(s, cancel).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            actual = null;
        }

        if (actual is null)
        {
            // the file vanished or was locked: Windows Security quarantines files a few seconds after they land
            await ctx.Delay(TimeSpan.FromSeconds(2), cancel).ConfigureAwait(false);
            if (!File.Exists(file))
            {
                return ChecksumOutcome.Removed;
            }

            log.Write($"  Could not read {Path.GetFileName(file)} to check it (another program has it locked). Not using it.", LogColor.Red);
            return ChecksumOutcome.Bad;
        }

        if (actual == p.Hash)
        {
            log.Write($"  SHA-256 matches the published checksum ({p.From}).", LogColor.Green);
            return ChecksumOutcome.Ok;
        }

        log.Write($"  SHA-256 does NOT match the published checksum ({p.From}). The download was deleted.", LogColor.Red);
        log.Write($"    expected {p.Hash}", LogColor.Red);
        log.Write($"    got      {actual}", LogColor.Red);
        return ChecksumOutcome.Bad;
    }

    [GeneratedRegex(@"^(sha256sums|checksums?|sha256|hashes)(\.txt|\.sha256)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ListName();

    [GeneratedRegex(@"\b([0-9a-f]{64})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Sha256();
}
