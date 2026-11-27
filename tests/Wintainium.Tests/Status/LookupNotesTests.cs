using System.Text.Json.Nodes;
using Wintainium.Core.Data;
using Wintainium.Core.Logging;
using Wintainium.Core.Releases;
using Wintainium.Core.Status;

namespace Wintainium.Tests.Status;

/// <summary>The notes checks of Test-Lookups.ps1, and the summary line under the title.</summary>
public class LookupNotesTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

    private static AppRow Row(string json, string status, StaleInfo? stale = null) => new()
    {
        Entry = new AppEntry(JsonNode.Parse(json)!.AsObject()),
        Status = status,
        Installed = "1.0",
        Latest = stale is null ? "" : "2.1.0 (cached)",
        Released = "",
        Methods = ["page"],
        Page = "",
        Exe = "",
        Stale = stale,
    };

    private static readonly AppRow ItchNew = Row("""{ "name": "ItchNew", "source": "itch", "url": "https://dev.itch.io/fresh" }""", Statuses.SiteBusy);
    private static readonly AppRow ItchCached = Row("""{ "name": "ItchCached", "source": "itch", "url": "https://dev.itch.io/cached" }""", Statuses.UpToDate,
        new StaleInfo(Statuses.SiteBusy, "itch.io", new DateTime(2026, 11, 27, 7, 0, 0, DateTimeKind.Utc)));
    private static readonly AppRow GhBadToken = Row("""{ "name": "GhBadToken", "source": "github", "repo": "o/badtoken" }""", Statuses.TokenRejected);
    private static readonly AppRow GhOk = Row("""{ "name": "GhOk", "source": "github", "repo": "o/ok" }""", Statuses.UpToDate);
    private static readonly AppRow HttpForge = Row("""{ "name": "HttpForge", "source": "gitea", "api": "http://git.example/api/v1/repos/x/y/releases/latest" }""", Statuses.LookupFailed);

    private static string Notes(IReadOnlyList<AppRow> rows, DateTime? expires = null)
    {
        var log = new MemoryLog();
        LookupNotes.Write(rows, log, expires, Now);
        return log.AllText;
    }

    private static string LookupsRun() => Notes([ItchNew, ItchCached, GhBadToken, GhOk, HttpForge]);

    [Fact(DisplayName = "token message shown once for the GitHub app")]
    public void Token_message_once() =>
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(LookupsRun(), "GitHub rejected the saved token"));

    [Fact(DisplayName = "no rate-limit message for an itch.io failure")]
    public void No_rate_limit_message_for_itch() => Assert.DoesNotContain("hourly lookup limit", LookupsRun(), StringComparison.Ordinal);

    [Fact(DisplayName = "site-busy note names itch.io")]
    public void Site_busy_names_itch() => Assert.Contains("ItchNew: itch.io didn't answer", LookupsRun(), StringComparison.Ordinal);

    [Fact(DisplayName = "fallback note says when the saved answer is from")]
    public void Fallback_note_says_when() =>
        Assert.Contains("ItchCached: itch.io didn't answer, so this shows what it reported at "
            + new DateTime(2026, 11, 27, 7, 0, 0, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            LookupsRun(), StringComparison.Ordinal);

    [Fact(DisplayName = "http release address explained")]
    public void Http_address_explained() => Assert.Contains("HttpForge: Wintainium only looks up releases over https", LookupsRun(), StringComparison.Ordinal);

    [Fact]
    public void Rate_limit_message_has_the_token_hint()
    {
        string text = Notes([Row("""{ "name": "L", "source": "github", "repo": "o/l" }""", Statuses.RateLimited)]);
        Assert.Contains("hourly lookup limit", text, StringComparison.Ordinal);
        Assert.Contains(LookupNotes.LimitHint, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(6.4, true, "6")]
    [InlineData(7.5, false, "")] // [int] rounds half to even: 8 days, no warning
    [InlineData(6.5, true, "6")]
    [InlineData(30, false, "")]
    public void Expiring_token_warned_within_a_week(double daysLeft, bool warned, string days)
    {
        string text = Notes([GhOk], Now.AddDays(daysLeft));
        Assert.Equal(warned, text.Contains("Your GitHub token expires in about " + days + " day(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void Summary_all_up_to_date()
    {
        (string text, StatusSummary.Tone tone) = StatusSummary.For([GhOk], new DateTime(2026, 11, 27, 9, 5, 0));
        Assert.Equal("All 1 apps are up to date  -  checked 09:05", text);
        Assert.Equal(StatusSummary.Tone.Good, tone);
    }

    [Fact]
    public void Summary_counts_updates_and_problems()
    {
        AppRow upd = Row("""{ "name": "U" }""", Statuses.Update);
        (string text, StatusSummary.Tone tone) = StatusSummary.For([upd, GhOk, ItchNew, GhBadToken], new DateTime(2026, 11, 27, 9, 5, 0));
        Assert.Equal("1 update available  -  2 need attention  -  4 apps  -  checked 09:05", text);
        Assert.Equal(StatusSummary.Tone.Updates, tone);
        (text, tone) = StatusSummary.For([ItchNew, GhOk], new DateTime(2026, 11, 27, 9, 5, 0));
        Assert.Equal("1 needs attention  -  2 apps  -  checked 09:05", text);
        Assert.Equal(StatusSummary.Tone.Problems, tone);
    }
}
