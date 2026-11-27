using System.Diagnostics;
using System.Globalization;
using Wintainium.Core.Logging;
using Wintainium.Core.Releases;

namespace Wintainium.Core.Install;

public enum DownloadOutcome
{
    Ok,
    Failed,

    /// <summary>Windows Security removed the file right after it landed.</summary>
    Removed,
}

/// <summary>
/// Downloads with a live size/time line, tags the file with Windows' "downloaded from the
/// internet" mark so SmartScreen still checks it, and notices when Windows Security removes the
/// file the moment it lands (Save-Download).
/// </summary>
public sealed class Downloader(InstallContext ctx)
{
    /// <summary>A download that receives nothing for this long is stopped.</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    public const string UserAgent = "Wintainium";

    public async Task<DownloadOutcome> SaveAsync(string url, string file, string label, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ILog log = ctx.Log;
        Uri? uri = Links.ParseHttps(url);
        string shown = Links.StripUserInfo(url ?? "");
        if (uri is null)
        {
            log.Write($"  Not downloading {label} from {shown}: Wintainium only downloads over https.", LogColor.Red);
            return DownloadOutcome.Failed;
        }

        log.Write($"  Downloading {label}...");
        var watch = Stopwatch.StartNew();
        long total = 0;
        try
        {
            // an earlier download of the same name that is still open somewhere fails here, inside the try
            if (File.Exists(file))
            {
                File.Delete(file);
            }

            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            stall.CancelAfter(StallTimeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.UserAgent.ParseAdd(UserAgent);
            using HttpResponseMessage resp = await ctx.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using Stream body = await resp.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var output = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, useAsync: true);
            byte[] buffer = new byte[81920];
            long lastShown = -1;
            int read;
            while ((read = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
            {
                stall.CancelAfter(StallTimeout);
                await output.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                total += read;
                long tenth = watch.ElapsedMilliseconds / 300;
                if (tenth != lastShown)
                {
                    lastShown = tenth;
                    log.Progress(string.Format(CultureInfo.InvariantCulture, "  downloading {0}... {1} MB, {2}s", label, Math.Round(total / 1048576.0, 1), (int)watch.Elapsed.TotalSeconds));
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            string why = ex is OperationCanceledException
                ? (cancel.IsCancellationRequested ? "cancelled" : $"no data for {StallTimeout.TotalSeconds:0} seconds")
                : ex.GetBaseException().Message;
            log.Write($"  Download failed: {why}", LogColor.Red);
            TryDelete(file);
            return DownloadOutcome.Failed;
        }

        // the byte count, not the file: Windows Security may already have removed it
        log.Progress(string.Format(CultureInfo.InvariantCulture, "  downloaded {0}, {1} MB in {2}s", label, Math.Round(total / 1048576.0, 1), (int)watch.Elapsed.TotalSeconds));
        if (File.Exists(file))
        {
            try
            {
                ctx.Mark.Mark(file, shown);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // the mark is a courtesy to SmartScreen; FAT32 or exFAT drives can't hold it,
                // and the file may be removed while the mark is written
            }
        }

        await ctx.Delay(TimeSpan.FromMilliseconds(1500), cancel).ConfigureAwait(false);
        return File.Exists(file) ? DownloadOutcome.Ok : DownloadOutcome.Removed;
    }

    /// <summary>Reads a small text file such as a checksum list. Null when it can't be read.</summary>
    public async Task<string?> GetTextAsync(string url, CancellationToken cancel = default)
    {
        Uri? uri = Links.ParseHttps(url);
        if (uri is null)
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(ReleaseSources.DefaultTimeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.UserAgent.ParseAdd(UserAgent);
            using HttpResponseMessage resp = await ctx.Http.SendAsync(req, timeout.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return null;
        }
    }

    internal static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // left for Windows Security or the user
        }
    }
}
