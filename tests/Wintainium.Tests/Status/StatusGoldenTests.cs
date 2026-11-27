using System.Globalization;
using System.Text.Json.Nodes;
using Wintainium.Core.Data;
using Wintainium.Core.Releases;
using Wintainium.Core.Status;

namespace Wintainium.Tests.Status;

/// <summary>
/// Runs the C# status code on the same cases as Make-StatusGolden.ps1 and compares with what
/// the PowerShell app (v0.3.0) answered. Only the listed intentional changes may differ.
/// </summary>
public class StatusGoldenTests
{
    /// <summary>Cases where v0.4.0 answers differently on purpose, with the new answer and why.</summary>
    private static readonly Dictionary<string, (string Status, string Why)> Deviations = new()
    {
        // X-10: 12.1.0 only "contained" 2.1. Not the same release, and the numbers can't be
        // compared (other scheme, installed higher), so the release date decides, as it does for
        // "Installed above release, other major" in v0.3.0
        ["Substring trap"] = ("UPDATE", "X-10: no substring match; the newer release date decides"),
        // X-09: a real installed version lower than the release is older, whatever the dates say
        ["Major jump, exe newer than release"] = ("UPDATE", "X-09: 1.9.5 is lower than 2.0.0"),
        ["Minor change, exe newer than release"] = ("UPDATE", "X-09: 2.9.181 is lower than 2.10.0"),
    };

    private sealed class CaseProbe(Dictionary<string, ExeInfo> byPath) : IExeProbe
    {
        public ExeInfo Probe(string path) => byPath.GetValueOrDefault(path, ExeInfo.Missing);
    }

    private static string GoldenPath(string name) => Path.Combine(AppContext.BaseDirectory, "golden", name);

    private static DateTime Utc(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture).UtcDateTime;

    private static List<AppRow> RunCases(string selfVersion)
    {
        string here = Path.Combine(Path.GetTempPath(), "wintainium-golden-self");
        var cases = JsonNode.Parse(File.ReadAllText(GoldenPath("status-cases.json")))!.AsArray();
        var probe = new Dictionary<string, ExeInfo>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<AppRow>();
        foreach (JsonNode? node in cases)
        {
            var c = node!.AsObject();
            var json = c["app"]!.AsObject().DeepClone().AsObject();
            json["exe"] = json["exe"]!.GetValue<string>().Replace("{HERE}/", here + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            var app = new AppEntry(json);
            var exe = c["exe"] is JsonObject e
                ? new ExeInfo(true, e["version"]?.GetValue<string>() ?? "", Utc(e["written"]!.GetValue<string>()))
                : ExeInfo.Missing;
            probe[app.ExpandedExe] = exe;

            LookupResult? lookup = null;
            if (!string.Equals(app.Source, "manual", StringComparison.OrdinalIgnoreCase))
            {
                if (c["fail"] is JsonNode f)
                {
                    lookup = new LookupResult { FailureStatus = f.GetValue<string>() };
                }
                else
                {
                    var r = c["release"]!.AsObject();
                    var rel = new ReleaseInfo
                    {
                        Tag = r["tag"]!.GetValue<string>().Replace("{SELF}", selfVersion, StringComparison.Ordinal),
                        Date = r["date"] is JsonNode d ? Utc(d.GetValue<string>()) : null,
                        Page = "https://example.org/release",
                    };
                    StaleInfo? stale = c["stale"] is JsonObject s
                        ? new StaleInfo(s["reason"]!.GetValue<string>(), s["site"]!.GetValue<string>(), Utc(s["fetched"]!.GetValue<string>()))
                        : null;
                    lookup = new LookupResult { Release = rel, Stale = stale };
                }
            }

            rows.Add(StatusEngine.BuildRow(app, lookup, new CaseProbe(probe).Probe(app.ExpandedExe), 3, goldSelf: true, new StatusEngine.SelfInfo(here, selfVersion)));
        }

        return [.. StatusEngine.Order(rows)];
    }

    [Fact]
    public void Every_case_matches_the_PowerShell_answer_except_the_listed_changes()
    {
        var golden = JsonNode.Parse(File.ReadAllText(GoldenPath("status-golden.json")))!.AsObject();
        string version = golden["version"]!.GetValue<string>();
        var want = golden["rows"]!.AsArray().Select(n => n!.AsObject()).ToList();
        var got = RunCases(version).ToDictionary(r => r.App);
        Assert.Equal(want.Count, got.Count);

        var problems = new List<string>();
        foreach (JsonObject w in want)
        {
            string name = w["App"]!.GetValue<string>();
            AppRow r = got[name];
            string status = w["Status"]!.GetValue<string>();
            if (Deviations.TryGetValue(name, out var dev))
            {
                Assert.NotEqual(status, dev.Status);
                status = dev.Status;
            }

            void Check(string field, string expected, string actual)
            {
                if (expected != actual)
                {
                    problems.Add($"{name}: {field} expected '{expected}', got '{actual}'");
                }
            }

            Check("Status", status, r.Status);
            Check("Installed", w["Installed"]!.GetValue<string>(), r.Installed);
            Check("Latest", w["Latest"]!.GetValue<string>(), r.Latest);
            Check("Released", w["Released"]!.GetValue<string>(), r.Released);
            Check("Via", w["Via"]!.GetValue<string>(), r.Via);
            Check("Gold", w["Gold"]!.GetValue<bool>().ToString(), r.Gold.ToString());
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Rows_come_in_the_PowerShell_order_apart_from_the_moved_statuses()
    {
        var golden = JsonNode.Parse(File.ReadAllText(GoldenPath("status-golden.json")))!.AsObject();
        var want = golden["rows"]!.AsArray().Select(n => n!["App"]!.GetValue<string>()).ToList();
        var got = RunCases(golden["version"]!.GetValue<string>()).Select(r => r.App).ToList();
        // the changed rows move between groups, so compare the order without them
        Assert.Equal(want.Where(n => !Deviations.ContainsKey(n)), got.Where(n => !Deviations.ContainsKey(n)));
    }
}
