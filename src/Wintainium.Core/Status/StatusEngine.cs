using System.Globalization;
using Wintainium.Core.Data;
using Wintainium.Core.Releases;

namespace Wintainium.Core.Status;

/// <summary>
/// Works out each app's status from its program and its latest release (Get-AppRows), and the
/// order rows are shown in.
/// </summary>
public static class StatusEngine
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["emudeck"] = "EmuDeck",
        ["inapp"] = "in-app",
        ["installer"] = "installer",
        ["zip"] = "zip install",
        ["download"] = "download",
        ["page"] = "release page",
    };

    private static readonly Dictionary<string, int> Rank = new(StringComparer.Ordinal)
    {
        [Statuses.Update] = 0,
        [Statuses.TokenRejected] = 1,
        [Statuses.RateLimited] = 1,
        [Statuses.SiteBusy] = 1,
        [Statuses.LookupFailed] = 1,
        [Statuses.ExeNotFound] = 2,
        [Statuses.CheckManually] = 3,
        [Statuses.UpToDate] = 4,
    };

    /// <summary>"Update via" label of a method; unknown methods show nothing.</summary>
    public static string MethodLabel(string method) => Labels.GetValueOrDefault(method ?? "", "");

    /// <summary>Where Wintainium itself runs from, so its own row shows its own version.</summary>
    public sealed record SelfInfo(string AppFolder, string Version);

    /// <summary>Builds one row. <paramref name="lookup"/> is null for manual apps.</summary>
    public static AppRow BuildRow(AppEntry app, LookupResult? lookup, ExeInfo exe, int defaultGraceDays, bool goldSelf, SelfInfo? self = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(exe);
        string exePath = app.ExpandedExe;
        string installedVer = exe.Exists ? exe.ProductVersion : "";
        if (exe.Exists && self is not null && IsOwnProgram(exePath, self.AppFolder))
        {
            // Wintainium's own entry: the app knows its version
            installedVer = self.Version;
        }

        ReleaseInfo? latest = null;
        string status = "";
        string page = app.Url;
        if (string.Equals(app.Source, "manual", StringComparison.OrdinalIgnoreCase))
        {
            status = Statuses.CheckManually;
        }
        else if (lookup is not null)
        {
            if (lookup.Release is not null)
            {
                latest = lookup.Release;
                page = latest.Page;
            }
            else
            {
                status = lookup.FailureStatus ?? Statuses.LookupFailed;
            }
        }

        if (!exe.Exists)
        {
            status = Statuses.ExeNotFound;
        }
        else if (latest is not null && app.HasInstalledTag)
        {
            // mods and data packs have no program to read a version from, so compare the
            // release that was installed (recorded by Add app and by each zip update)
            status = string.Equals(latest.Tag, app.InstalledTag, StringComparison.OrdinalIgnoreCase) ? Statuses.UpToDate : Statuses.Update;
        }
        else if (latest is not null)
        {
            int grace = app.GraceDays ?? defaultGraceDays;
            string num = VersionCompare.TagNumber(latest.Tag);
            bool? older = VersionCompare.IsOlder(installedVer, num);
            if (VersionCompare.IsSameRelease(installedVer, num))
            {
                status = Statuses.UpToDate;
            }
            else if (older is bool o)
            {
                // same numbering scheme on both sides (2.9.179 vs 2.9.181): the numbers decide
                status = o ? Statuses.Update : Statuses.UpToDate;
            }
            else if (latest.Date is DateTime d && exe.LastWriteUtc is DateTime w && d > w.AddDays(grace))
            {
                status = Statuses.Update;
            }
            else
            {
                status = Statuses.UpToDate;
            }
        }

        IReadOnlyList<string> methods = app.Update;
        string installed = installedVer.Length > 0 ? installedVer
            : app.HasInstalledTag ? app.InstalledTag
            : exe.Exists && exe.LastWriteUtc is DateTime lw ? lw.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "";
        StaleInfo? stale = latest is not null ? lookup?.Stale : null;
        return new AppRow
        {
            Entry = app,
            Status = status,
            Installed = installed,
            Latest = latest is null ? "" : latest.Tag + (stale is not null ? " (cached)" : ""),
            LatestTag = latest?.Tag ?? "",
            Stale = stale,
            Released = latest?.Date is DateTime rd ? rd.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "",
            Methods = methods,
            Page = page,
            Exe = exePath,
            Assets = latest?.Assets ?? [],
            Notes = latest?.Notes ?? "",
            Gold = app.IsSelf && goldSelf,
        };
    }

    /// <summary>UPDATE first, then problems, missing programs, manual checks, up to date; then by name.</summary>
    public static IReadOnlyList<AppRow> Order(IEnumerable<AppRow> rows)
    {
        StringComparer byName = StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true);
        return [.. rows.OrderBy(r => Rank.TryGetValue(r.Status, out int k) ? k : -1).ThenBy(r => r.App, byName)];
    }

    private static bool IsOwnProgram(string exePath, string appFolder)
    {
        if (string.IsNullOrEmpty(appFolder))
        {
            return false;
        }

        return string.Equals(exePath, Path.Combine(appFolder, "Wintainium.exe"), StringComparison.OrdinalIgnoreCase)
            || string.Equals(exePath, Path.Combine(appFolder, "Wintainium.ps1"), StringComparison.OrdinalIgnoreCase);
    }
}
