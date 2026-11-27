using System.Globalization;
using System.Text.Json.Nodes;
using Wintainium.Core.Logging;
using Wintainium.Core.Token;

namespace Wintainium.Core.Session;

/// <summary>What the setup wizard says and saves (Show-SetupWizard, Complete-Wizard), kept out of the window so it is tested.</summary>
public static class SetupSteps
{
    public const int Pages = 5;

    public static string StepLabel(int page) => $"Step {page} of {Pages}";

    /// <summary>N-04: the line under the token box before anything is checked.</summary>
    public static string TokenStatus(bool saved, DateTime? expires)
    {
        if (!saved)
        {
            return "No token saved. You can skip this with Next and add one later with Setup.";
        }

        string until = expires is DateTime e ? "expires " + e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "no expiry date recorded";
        return $"A token is already saved ({until}). Paste a new one only if you want to replace it, or click Next.";
    }

    /// <summary>N-04: checks a pasted token and saves it only when it is safe. Returns the text to show and whether it was saved.</summary>
    public static async Task<(string Text, bool Saved)> CheckAndSaveTokenAsync(HttpClient http, TokenStore store, string? token, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        TokenCheckResult t = await GitHubTokenCheck.CheckAsync(http, token, cancel).ConfigureAwait(false);
        if (!t.Ok || !t.Safe)
        {
            return ($"{t.Message} Nothing was saved.", false);
        }

        store.Save(token!, t.Expires);
        string until = t.Expires is DateTime e ? $"It expires {e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}." : "It has no expiry date.";
        return ($"Saved. {t.Message} GitHub now allows {t.Limit} lookups an hour. {until}", true);
    }

    /// <summary>N-05: the line under Scan this PC once the scan window closed.</summary>
    public static string ScanResult(int added) => added > 0
        ? $"Added {added} app{(added != 1 ? "s" : "")} to your list. Click Next."
        : "Nothing added. You can scan again any time with the Scan button.";

    /// <summary>
    /// N-07: saves the folders (empty boxes keep the current ones) and setupDone, and logs it.
    /// Returns the folders saved.
    /// </summary>
    public static (string InstallRoot, string DownloadDir) Finish(AppSession session, string? installBox, string? downloadBox)
    {
        ArgumentNullException.ThrowIfNull(session);
        string inst = (installBox ?? "").Trim();
        string dl = (downloadBox ?? "").Trim();
        if (inst.Length == 0)
        {
            inst = session.InstallRoot;
        }

        if (dl.Length == 0)
        {
            dl = session.DownloadDir;
        }

        session.Settings.Save(new Dictionary<string, JsonNode?>
        {
            ["installRoot"] = inst,
            ["downloadDir"] = dl,
            ["setupDone"] = true,
        });
        session.Log.Write($"Setup saved. New apps go to {inst}, downloads to {dl}.", LogColor.Green);
        return (inst, dl);
    }

    /// <summary>N-08: closing or cancelling still counts as seen, so the wizard doesn't open on every start.</summary>
    public static void MarkSeen(AppSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.Settings.SetupDone)
        {
            session.Settings.Save("setupDone", true);
        }
    }
}
