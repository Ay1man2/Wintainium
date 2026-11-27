using System.Globalization;
using Wintainium.Core.Logging;
using Wintainium.Core.Releases;

namespace Wintainium.Core.Status;

/// <summary>Explains failed or fallback lookups in the log pane (Show-LookupNotes).</summary>
public static class LookupNotes
{
    public const string TokenHint = "Click Setup to save a new one.";
    public const string LimitHint = "Click Setup to add a free GitHub token (5,000 lookups an hour).";

    public static void Write(IReadOnlyList<AppRow> rows, ILog log, DateTime? tokenExpires, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(log);
        var reasons = rows.Select(r => r.Stale?.Reason ?? r.Status).ToList();
        if (reasons.Contains(Statuses.TokenRejected))
        {
            log.Write($"GitHub rejected the saved token (expired or deleted). {TokenHint}", LogColor.Yellow);
        }
        else if (reasons.Contains(Statuses.RateLimited))
        {
            log.Write($"GitHub's hourly lookup limit was reached (60 without a token). It resets within the hour. {LimitHint}", LogColor.Yellow);
        }
        else if (tokenExpires is DateTime exp)
        {
            // PowerShell's [int] rounds half to even
            int left = (int)Math.Round((exp - now).TotalDays, MidpointRounding.ToEven);
            if (left <= 7)
            {
                log.Write($"Your GitHub token expires in about {left} day(s). {TokenHint}", LogColor.Yellow);
            }
        }

        foreach (AppRow r in rows.Where(r => r.Status == Statuses.LookupFailed))
        {
            string? u = ReleaseSources.LookupUrl(r.Entry);
            if (!string.IsNullOrEmpty(u) && !Links.IsHttps(u))
            {
                log.Write($"{r.App}: Wintainium only looks up releases over https, and its address ({u}) isn't one. Change it to https:// in wintainium-apps.json.", LogColor.Yellow);
            }
        }

        foreach (AppRow r in rows.Where(r => r.Status == Statuses.SiteBusy))
        {
            log.Write($"{r.App}: {ReleaseSources.SiteName(r.Entry)} didn't answer. It's usually busy for a few minutes; try Refresh later.", LogColor.Yellow);
        }

        foreach (AppRow r in rows.Where(r => r.Stale is not null))
        {
            StaleInfo s = r.Stale!;
            string when = s.FetchedUtc is DateTime f ? f.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "an earlier check";
            string what = s.Reason switch
            {
                Statuses.TokenRejected => "rejected the GitHub token",
                Statuses.RateLimited => "hit its hourly limit",
                Statuses.SiteBusy => "didn't answer",
                _ => "gave an error",
            };
            log.Write($"{r.App}: {s.Site} {what}, so this shows what it reported at {when}.", LogColor.DarkGray);
        }
    }
}

/// <summary>The line under the title (Update-Grid's summary).</summary>
public static class StatusSummary
{
    public enum Tone
    {
        Good,
        Updates,
        Problems,
    }

    public static (string Text, Tone Tone) For(IReadOnlyList<AppRow> rows, DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(rows);
        int n = rows.Count(r => r.Status == Statuses.Update);
        int bad = rows.Count(r => r.Status is not (Statuses.Update or Statuses.UpToDate or Statuses.CheckManually));
        string when = "checked " + localNow.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (n == 0 && bad == 0)
        {
            return ($"All {rows.Count} apps are up to date  -  {when}", Tone.Good);
        }

        var parts = new List<string>();
        if (n > 0)
        {
            parts.Add($"{n} update{(n != 1 ? "s" : "")} available");
        }

        if (bad > 0)
        {
            parts.Add($"{bad} need{(bad == 1 ? "s" : "")} attention");
        }

        parts.Add($"{rows.Count} apps");
        parts.Add(when);
        return (string.Join("  -  ", parts), n > 0 ? Tone.Updates : Tone.Problems);
    }
}
