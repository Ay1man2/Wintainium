using System.Text.RegularExpressions;
using Wintainium.Core.Logging;
using Wintainium.Core.Releases;

namespace Wintainium.Core.Install;

/// <summary>
/// Windows Security removed a file: explain, and offer the safe ways to check it
/// (Show-QuarantineHelp). This never restores files, allows threats or adds exclusions: that
/// stays the user's decision.
/// </summary>
public static partial class QuarantineHelp
{
    public const string VirusTotalBase = "https://www.virustotal.com/gui/file/";
    public const string FalsePositiveForm = "https://www.microsoft.com/en-us/wdsi/filesubmission";

    public static async Task ShowAsync(IReadOnlyList<ReleaseAsset> assets, string name, InstallContext ctx, Downloader downloader, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ILog log = ctx.Log;
        log.Write($"  Windows Security removed {name}.", LogColor.Red);
        try
        {
            ThreatDetection? det = ctx.Threats?.FindRecent(name);
            if (det is not null)
            {
                log.Write($"  Detection: {det.ThreatName} (threat ID {det.ThreatId})", LogColor.Red);
                if (GenericDetection().IsMatch(det.ThreatName))
                {
                    log.Write("  That is a generic machine-learning detection, which small unsigned tools often trigger.", LogColor.Yellow);
                    log.Write("  It is not proof either way.", LogColor.Yellow);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            log.Write("  (Run as administrator to see the detection name here.)", LogColor.DarkGray);
        }

        log.Write("  Do not use \"Allow on device\": it allows the threat ID, which for a generic detection", LogColor.Yellow);
        log.Write("  also covers unrelated files. Avoid folder exclusions for the same reason.", LogColor.Yellow);
        PublishedHash? p = await Checksums.FindAsync(assets, name, downloader, cancel).ConfigureAwait(false);
        if (p?.Hash is not null)
        {
            log.Write($"  Published SHA-256 ({p.From}): {p.Hash}");
            if (ctx.Prompts.Confirm("Look this hash up on VirusTotal (no upload needed)?"))
            {
                WebPage.Open(VirusTotalBase + p.Hash, ctx.Shell, log);
            }
        }

        if (ctx.Prompts.Confirm("Open Microsoft's form to report a false positive?"))
        {
            WebPage.Open(FalsePositiveForm, ctx.Shell, log);
        }
    }

    [GeneratedRegex("!ml$|Wacatac", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenericDetection();
}
