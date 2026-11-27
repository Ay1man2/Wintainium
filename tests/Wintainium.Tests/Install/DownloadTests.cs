using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Wintainium.Core.Data;
using Wintainium.Core.Install;
using Wintainium.Core.Releases;
using Wintainium.Core.Status;
using Wintainium.Tests.Helpers;

namespace Wintainium.Tests.Install;

/// <summary>The checks of Test-Downloads.ps1: downloads, checksums, safe names, links, Windows Security.</summary>
public sealed class DownloadTests : IDisposable
{
    private const string Base = "https://dl.example/rel/";
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("pretend this is a zip");
    private static readonly string Hash = Convert.ToHexStringLower(SHA256.HashData(Payload));
    private readonly InstallRig rig;

    public DownloadTests()
    {
        var http = new FakeHttp()
            .On(Base + "Tool-v1.2-Windows-x64.zip", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) })
            .On(Base + "Tool-v1.2-Windows-x64.zip.sha256", _ => FakeHttp.Text($"{Hash}  Tool-v1.2-Windows-x64.zip\n"))
            .On(Base + "bad.sha256", _ => FakeHttp.Text(new string('0', 64)))
            .On(Base + "SHA256SUMS", _ => FakeHttp.Text($"{new string('1', 64)}  other.zip\n{Hash}  Tool-v1.2-Windows-x64.zip\n"))
            .On(Base + "empty.sha256", _ => FakeHttp.Text("not a hash"));
        rig = new InstallRig(http);
    }

    public void Dispose() => rig.Dispose();

    private static AppRow Row(string json, params ReleaseAsset[] assets) => new()
    {
        Entry = new AppEntry(JsonNode.Parse(json)!.AsObject()),
        Status = Statuses.Update,
        Installed = "1.1",
        Latest = "v1.2",
        LatestTag = "v1.2",
        Released = "2026-11-20",
        Methods = ["download", "page"],
        Page = "https://gh/tool/releases/tag/v1.2",
        Exe = "",
        Assets = assets,
    };

    private static ReleaseAsset Zip => new("Tool-v1.2-Windows-x64.zip", Base + "Tool-v1.2-Windows-x64.zip");

    private Updater Updater => new(rig.Ctx, null);

    private string DlFile(string name) => Path.Combine(rig.Ctx.DownloadDir, name);

    [Fact(DisplayName = "download via progress loop lands the file")]
    public async Task Download_lands_the_file()
    {
        string? f = await Updater.GetAssetFileAsync(Row("""{ "name": "Tool" }""", Zip));
        Assert.Equal(DlFile(Zip.Name), f);
        Assert.Equal(Payload, File.ReadAllBytes(f!));
        Assert.Contains("downloaded Tool-v1.2-Windows-x64.zip", rig.Log.LastProgress, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Downloaded file gets the download mark")]
    public async Task Download_is_marked()
    {
        string? f = await Updater.GetAssetFileAsync(Row("""{ "name": "Tool" }""", Zip));
        Assert.Equal($"[ZoneTransfer]\r\nZoneId=3\r\nHostUrl={Zip.Url}\r\n", rig.Mark.Read(f!));
    }

    [WindowsFact]
    public void Zone_identifier_round_trips_on_NTFS()
    {
        string f = rig.Dir.Write("dl/marked.zip", "x");
        var mark = new ZoneIdentifierMark();
        mark.Mark(f, "https://dl.example/marked.zip");
        Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://dl.example/marked.zip\r\n", mark.Read(f));
        Assert.Null(mark.Read(rig.Dir.Write("dl/plain.zip", "x")));
    }

    [Fact(DisplayName = "per-file .sha256 verified")]
    public async Task Per_file_sha256_verified()
    {
        string? f = await Updater.GetAssetFileAsync(Row("""{ "name": "Tool", "asset": "x64\\.zip$" }""", Zip, new(Zip.Name + ".sha256", Base + "Tool-v1.2-Windows-x64.zip.sha256")));
        Assert.NotNull(f);
        Assert.Contains("SHA-256 matches the published checksum (Tool-v1.2-Windows-x64.zip.sha256).", rig.Log.AllText, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "bad checksum rejected; bad download deleted")]
    public async Task Bad_checksum_rejected_and_deleted()
    {
        string? f = await Updater.GetAssetFileAsync(Row("""{ "name": "Tool", "asset": "x64\\.zip$" }""", Zip, new(Zip.Name + ".sha256", Base + "bad.sha256")));
        Assert.Null(f);
        Assert.Contains("does NOT match", rig.Log.AllText, StringComparison.Ordinal);
        Assert.Contains("    got      " + Hash, rig.Log.AllText, StringComparison.Ordinal);
        Assert.False(File.Exists(DlFile(Zip.Name)));
    }

    [Fact(DisplayName = "SHA256SUMS list verified")]
    public async Task Sums_list_verified()
    {
        string? f = await Updater.GetAssetFileAsync(Row("""{ "name": "Tool", "asset": "x64\\.zip$" }""", Zip, new("SHA256SUMS", Base + "SHA256SUMS")));
        Assert.NotNull(f);
        Assert.Contains("(SHA256SUMS)", rig.Log.AllText, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "no checksum: download kept + noted")]
    public async Task No_checksum_kept_and_noted()
    {
        Assert.NotNull(await Updater.GetAssetFileAsync(Row("""{ "name": "Tool" }""", Zip)));
        Assert.Contains("No checksum is published for this file", rig.Log.AllText, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "X-03: a checksum file that can't be used refuses the download")]
    [InlineData("checksums.txt", Base + "empty.sha256", "does not list this file")]
    [InlineData("Tool-v1.2-Windows-x64.zip.sha256", "http://dl.example/rel/x.sha256", "is not an https link")]
    [InlineData("Tool-v1.2-Windows-x64.zip.sha256", Base + "missing.sha256", "Could not read")]
    public async Task Unusable_checksum_refuses(string name, string url, string why)
    {
        Assert.Null(await Updater.GetAssetFileAsync(Row("""{ "name": "Tool", "asset": "x64\\.zip$" }""", Zip, new(name, url))));
        Assert.Contains(why, rig.Log.AllText, StringComparison.Ordinal);
        Assert.Contains("integrity check could not be done", rig.Log.AllText, StringComparison.Ordinal);
        Assert.False(File.Exists(DlFile(Zip.Name)));
    }

    [Fact(DisplayName = "http checksum file not fetched")]
    public async Task Http_checksum_not_fetched()
    {
        await Updater.GetAssetFileAsync(Row("""{ "name": "Tool", "asset": "x64\\.zip$" }""", Zip, new(Zip.Name + ".sha256", "http://dl.example/rel/x.sha256")));
        Assert.DoesNotContain(rig.Http.Requests, r => r.RequestUri!.Scheme == "http");
    }

    [Fact(DisplayName = "missing file reported as failed download")]
    public async Task Missing_file_failed()
    {
        Assert.Null(await Updater.GetAssetFileAsync(Row("""{ "name": "Tool" }""", new ReleaseAsset("gone.zip", Base + "gone.zip"))));
        Assert.Contains("Download failed", rig.Log.AllText, StringComparison.Ordinal);
        Assert.False(File.Exists(DlFile("gone.zip")));
    }

    [Fact(DisplayName = "downloadUrl fills {version}")]
    public void DownloadUrl_fills_version()
    {
        ReleaseAsset? a = AssetPicker.Select(Row("""{ "name": "Tool", "downloadUrl": "https://dl.example/{version}/tool-{version}.zip" }"""), rig.Prompts);
        Assert.Equal(new ReleaseAsset("tool-1.2.zip", "https://dl.example/1.2/tool-1.2.zip"), a);
    }

    [Fact]
    public void Asset_pattern_picks_one_file_without_asking()
    {
        AppRow row = Row("""{ "name": "Tool", "asset": "windows.*\\.zip$" }""", Zip, new("Tool-v1.2-linux.AppImage", Base + "a"));
        Assert.Equal(Zip, AssetPicker.Select(row, rig.Prompts));
        Assert.Empty(rig.Prompts.Asked);
    }

    [Fact]
    public void Several_files_are_a_numbered_choice_and_Enter_cancels()
    {
        AppRow row = Row("""{ "name": "Tool" }""", Zip, new("Tool-v1.2-arm64.zip", Base + "b"));
        rig.Prompts.Choices.Enqueue(1);
        Assert.Equal("Tool-v1.2-arm64.zip", AssetPicker.Select(row, rig.Prompts)!.Name);
        rig.Prompts.Choices.Enqueue(null);
        Assert.Null(AssetPicker.Select(row, rig.Prompts));
    }

    [Fact]
    public async Task No_matching_download_opens_the_release_page()
    {
        Assert.Null(await Updater.GetAssetFileAsync(Row("""{ "name": "Tool", "asset": "nothing" }""", Zip)));
        Assert.Contains("No matching download on that release", rig.Log.AllText, StringComparison.Ordinal);
        Assert.Equal(["https://gh/tool/releases/tag/v1.2"], rig.Shell.Opened);
    }

    [Theory(DisplayName = "safe name: '<name>' stays in the download folder")]
    [InlineData(@"..\..\x.exe")]
    [InlineData("../x.exe")]
    [InlineData(@"C:\x.exe")]
    [InlineData(@"\\host\share\x.exe")]
    [InlineData(@"sub/dir\x.exe")]
    public void Safe_name_stays_inside(string name)
    {
        string? p = DownloadPaths.GetSafe(rig.Ctx.DownloadDir, name);
        Assert.Equal(Path.Combine(Path.GetFullPath(rig.Ctx.DownloadDir), "x.exe"), p);
    }

    [Theory(DisplayName = "safe name: '<name>' refused")]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"..\")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("COM1.zip")]
    [InlineData("lpt9")]
    [InlineData("CONIN$")]
    [InlineData("conout$.txt")]
    [InlineData("NUL .txt")]
    [InlineData("COM\u00B9.zip")]
    [InlineData("LPT\u00B3")]
    [InlineData("x.exe.")]
    [InlineData("x.exe ")]
    [InlineData("a:b.zip")]
    [InlineData("a?.zip")]
    [InlineData("a\tb.zip")]
    public void Safe_name_refused(string name) => Assert.Null(DownloadPaths.GetSafe(rig.Ctx.DownloadDir, name));

    [Theory(DisplayName = "safe name: '<name>' allowed")]
    [InlineData("CONSOLE.zip")]
    [InlineData("com10.exe")]
    [InlineData("nul-tool.zip")]
    [InlineData("AUXtool.7z")]
    [InlineData("Tool-v1.2-Windows-x64.zip")]
    public void Safe_name_allowed(string name) => Assert.Equal(name, Path.GetFileName(DownloadPaths.GetSafe(rig.Ctx.DownloadDir, name)));

    [Fact(DisplayName = "traversal name: file lands inside the download folder, the target is not touched, nothing started")]
    public async Task Traversal_name_lands_inside()
    {
        string decoy = rig.Dir.Write("escape/Tool-v1.2-Windows-x64.zip", "keep me");
        string? f = await Updater.GetAssetFileAsync(Row("""{ "name": "Tool" }""", new ReleaseAsset(@"..\escape\Tool-v1.2-Windows-x64.zip", Zip.Url)));
        Assert.StartsWith(Path.GetFullPath(rig.Ctx.DownloadDir), f, StringComparison.Ordinal);
        Assert.Equal("keep me", File.ReadAllText(decoy));
        Assert.Empty(rig.Shell.Started);
    }

    [Fact(DisplayName = "reserved name: refused with a message, no download")]
    public async Task Reserved_name_refused()
    {
        Assert.Null(await Updater.GetAssetFileAsync(Row("""{ "name": "Tool" }""", new ReleaseAsset("CON.zip", Zip.Url))));
        Assert.Contains("unsafe file name", rig.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Downloading ", rig.Log.AllText, StringComparison.Ordinal);
        Assert.Empty(rig.Http.Requests);
    }

    [Theory(DisplayName = "http download refused with a message; existing file not deleted")]
    [InlineData("http://dl.example/Tool.zip")]
    [InlineData("file:///C:/Windows/notepad.exe")]
    public async Task Non_https_download_refused(string url)
    {
        string existing = rig.Dir.Write("dl/Tool.zip", "old");
        Assert.Equal(DownloadOutcome.Failed, await new Downloader(rig.Ctx).SaveAsync(url, existing, "Tool.zip"));
        Assert.Contains("Wintainium only downloads over https", rig.Log.AllText, StringComparison.Ordinal);
        Assert.Equal("old", File.ReadAllText(existing));
        Assert.Empty(rig.Http.Requests);
    }

    [Fact]
    public async Task Passwords_in_a_refused_link_are_not_shown()
    {
        await new Downloader(rig.Ctx).SaveAsync("http://user:secret@dl.example/x.zip", DlFile("x.zip"), "x.zip");
        Assert.DoesNotContain("secret", rig.Log.AllText, StringComparison.Ordinal);
    }

    /// <summary>Windows Security removes the file during the wait right after the download.</summary>
    private async Task<(string? File, ThreatDetection Det)> Quarantined()
    {
        var det = new ThreatDetection("Trojan:Win32/Wacatac.B!ml", 2147735505);
        rig.Threats = new FakeThreats(det);
        InstallContext ctx = rig.Rebuild();
        var quarantining = new InstallContext
        {
            Http = ctx.Http, Log = ctx.Log, Prompts = ctx.Prompts, Shell = ctx.Shell, Mark = ctx.Mark, Threats = ctx.Threats,
            Processes = ctx.Processes, DownloadDir = ctx.DownloadDir, TempDir = ctx.TempDir, Now = ctx.Now,
            Delay = (_, _) =>
            {
                File.Delete(DlFile(Zip.Name));
                return Task.CompletedTask;
            },
        };
        rig.Prompts.Confirms.Enqueue(true);
        rig.Prompts.Confirms.Enqueue(true);
        AppRow row = Row("""{ "name": "Tool", "asset": "x64\\.zip$" }""", Zip, new ReleaseAsset(Zip.Name + ".sha256", Base + "Tool-v1.2-Windows-x64.zip.sha256"));
        return (await new Updater(quarantining, null).GetAssetFileAsync(row), det);
    }

    [Fact(DisplayName = "quarantine: names the detection, warns, shows the SHA-256, offers VirusTotal and Microsoft's form, returns nothing")]
    public async Task Quarantine_help()
    {
        (string? f, ThreatDetection det) = await Quarantined();
        string log = rig.Log.AllText;
        Assert.Null(f);
        Assert.Contains("Windows Security removed Tool-v1.2-Windows-x64.zip.", log, StringComparison.Ordinal);
        Assert.Contains($"Detection: {det.ThreatName} (threat ID {det.ThreatId})", log, StringComparison.Ordinal);
        Assert.Contains("generic machine-learning detection", log, StringComparison.Ordinal);
        Assert.Contains("Do not use \"Allow on device\"", log, StringComparison.Ordinal);
        Assert.Contains($"Published SHA-256 (Tool-v1.2-Windows-x64.zip.sha256): {Hash}", log, StringComparison.Ordinal);
        Assert.Equal([QuarantineHelp.VirusTotalBase + Hash, QuarantineHelp.FalsePositiveForm], rig.Shell.Opened);
        Assert.Empty(rig.Shell.Started);
    }

    [Fact]
    public async Task Quarantine_without_admin_rights_says_so()
    {
        rig.Threats = new FakeThreats(null, needsAdmin: true);
        var assets = new List<ReleaseAsset>();
        await QuarantineHelp.ShowAsync(assets, "x.zip", rig.Rebuild(), new Downloader(rig.Ctx));
        Assert.Contains("Run as administrator to see the detection name", rig.Log.AllText, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "late quarantine: no crash; reported as removed")]
    public async Task Late_quarantine_reported()
    {
        // the checksum can't read the file and it is gone after the wait
        string missing = rig.Dir.File("never-there.zip");
        var downloader = new Downloader(rig.Ctx);
        ChecksumOutcome r = await Checksums.VerifyAsync(
            [Zip, new ReleaseAsset(Zip.Name + ".sha256", Base + "Tool-v1.2-Windows-x64.zip.sha256")], Zip.Name, missing, rig.Ctx, downloader);
        Assert.Equal(ChecksumOutcome.Removed, r);
    }

    [Fact(DisplayName = "page link: https opened in its checked, parsed form")]
    public void Page_link_opened_parsed()
    {
        Assert.True(WebPage.Open("HTTPS://Example.org/a b", rig.Shell, rig.Log));
        Assert.Equal(["https://example.org/a%20b"], rig.Shell.Opened);
    }

    [Theory(DisplayName = "page link: '<url>' not opened, and said so")]
    [InlineData("http://example.org")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData(@"\\server\share\x.html")]
    [InlineData("javascript:alert(1)")]
    [InlineData("example.org/page")]
    [InlineData("")]
    public void Page_link_refused(string url)
    {
        Assert.False(WebPage.Open(url, rig.Shell, rig.Log));
        Assert.Empty(rig.Shell.Opened);
        Assert.Contains("it isn't an https link", rig.Log.AllText, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "no matching download: UNC page link not opened")]
    public async Task Unc_page_not_opened()
    {
        AppRow row = Row("""{ "name": "Tool", "asset": "nothing" }""", Zip);
        row = new AppRow
        {
            Entry = row.Entry, Status = row.Status, Installed = row.Installed, Latest = row.Latest, LatestTag = row.LatestTag,
            Released = row.Released, Methods = row.Methods, Page = @"\\server\share\page.html", Exe = row.Exe, Assets = row.Assets,
        };
        await Updater.GetAssetFileAsync(row);
        Assert.Empty(rig.Shell.Opened);
    }

    [Fact(DisplayName = "only the link opener can start a URL")]
    public void Only_WebPage_opens_links()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Wintainium.sln")))
        {
            dir = Path.GetDirectoryName(dir)!;
        }

        var callers = Directory.GetFiles(Path.Combine(dir, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(".OpenUrl(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Equal(["WebPage.cs"], callers);
    }
}
