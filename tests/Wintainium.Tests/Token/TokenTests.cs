using System.Diagnostics;
using System.Net;
using System.Text;
using Wintainium.Core.Logging;
using Wintainium.Core.Token;
using Wintainium.Tests.Helpers;

namespace Wintainium.Tests.Token;

public class TokenTests
{
    private static FakeHttp GitHub(string scopesFor = "ghp_scoped")
    {
        var http = new FakeHttp();
        http.On(GitHubTokenCheck.RateLimitUrl, req =>
        {
            string auth = req.Headers.Authorization?.ToString() ?? "";
            if (auth == "Bearer ghp_wrong")
            {
                return FakeHttp.Json("""{"message":"Bad credentials"}""", HttpStatusCode.Unauthorized);
            }

            HttpResponseMessage r = FakeHttp.Json("""{"rate":{"limit":5000,"remaining":4999}}""");
            r.Headers.Add("X-OAuth-Scopes", auth == $"Bearer {scopesFor}" ? "repo, workflow" : "");
            r.Headers.Add("GitHub-Authentication-Token-Expiration", "2027-11-18 10:00:00 -0500");
            return r;
        });
        return http;
    }

    [Fact(DisplayName = "expiry with offset parsed")]
    public void Expiry_with_offset_parsed()
    {
        DateTime? e = GitHubTokenCheck.ParseExpiry("2027-01-14 13:46:22 -0500");
        Assert.Equal("2027-01-14 18:46", e!.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact(DisplayName = "expiry in UTC parsed")]
    public void Expiry_in_utc_parsed()
    {
        DateTime? e = GitHubTokenCheck.ParseExpiry("2027-01-14 18:46:22 UTC");
        Assert.Equal("18:46", e!.Value.ToUniversalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(GitHubTokenCheck.ParseExpiry("next tuesday"));
    }

    [Fact(DisplayName = "no-scope token accepted and marked safe")]
    public async Task No_scope_token_accepted()
    {
        using HttpClient client = GitHub().Client();
        TokenCheckResult t = await GitHubTokenCheck.CheckAsync(client, "  ghp_good  ");
        Assert.True(t.Ok && t.Safe);
        Assert.Equal(5000, t.Limit);
        Assert.Equal(2027, t.Expires!.Value.Year);
    }

    [Fact(DisplayName = "token with scopes is flagged, not safe")]
    public async Task Scoped_token_flagged()
    {
        using HttpClient client = GitHub().Client();
        TokenCheckResult t = await GitHubTokenCheck.CheckAsync(client, "ghp_scoped");
        Assert.True(t.Ok);
        Assert.False(t.Safe);
        Assert.Equal("repo, workflow", t.Scopes);
        Assert.Contains("only needs a token with none", t.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "fine-grained token not treated as safe (permissions unknowable)")]
    public async Task Fine_grained_not_safe()
    {
        using HttpClient client = GitHub().Client();
        TokenCheckResult t = await GitHubTokenCheck.CheckAsync(client, "github_pat_fine");
        Assert.True(t.Ok && t.FineGrained);
        Assert.False(t.Safe);
        Assert.Contains("classic token", t.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "rejected token reported")]
    public async Task Rejected_token_reported()
    {
        using HttpClient client = GitHub().Client();
        TokenCheckResult t = await GitHubTokenCheck.CheckAsync(client, "ghp_wrong");
        Assert.False(t.Ok);
        Assert.Contains("did not accept", t.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "empty token reported")]
    public async Task Empty_token_reported()
    {
        var http = GitHub();
        using HttpClient client = http.Client();
        TokenCheckResult t = await GitHubTokenCheck.CheckAsync(client, "   ");
        Assert.False(t.Ok);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Token_is_only_sent_to_the_GitHub_API()
    {
        var http = GitHub();
        using HttpClient client = http.Client();
        await GitHubTokenCheck.CheckAsync(client, "ghp_good");
        Assert.All(http.Requests, r => Assert.Equal("api.github.com", r.RequestUri!.Host));
    }

    [Fact]
    public void Token_file_round_trips_in_the_Clixml_format()
    {
        using var dir = new TempDir();
        string path = dir.File("wintainium-token.xml");
        var expires = new DateTime(2027, 11, 18, 10, 0, 0, DateTimeKind.Local);
        TokenFile.Write(path, new StoredToken("ghp_abc123", expires), new FakeProtector());

        string xml = File.ReadAllText(path, Encoding.Unicode);
        Assert.Contains("<SS N=\"Token\">d9", xml, StringComparison.Ordinal);
        Assert.Contains("System.Management.Automation.PSCustomObject", xml, StringComparison.Ordinal);
        StoredToken t = TokenFile.Read(path, new FakeProtector());
        Assert.Equal("ghp_abc123", t.Token);
        Assert.Equal(expires, t.Expires);
    }

    [Fact]
    public void Reads_a_file_written_by_Export_Clixml()
    {
        using var dir = new TempDir();
        // What Export-Clixml writes for [pscustomobject]@{ Token = <SecureString>; Expires = $null }
        byte[] secret = new FakeProtector().Protect(Encoding.Unicode.GetBytes("ghp_fromPowerShell"));
        string xml = $"""
            <Objs Version="1.1.0.1" xmlns="http://schemas.microsoft.com/powershell/2004/04">
              <Obj RefId="0">
                <TN RefId="0">
                  <T>System.Management.Automation.PSCustomObject</T>
                  <T>System.Object</T>
                </TN>
                <MS>
                  <SS N="Token">{Convert.ToHexStringLower(secret)}</SS>
                  <Nil N="Expires" />
                </MS>
              </Obj>
            </Objs>
            """;
        string path = dir.File("wintainium-token.xml");
        File.WriteAllText(path, xml, new UnicodeEncoding(false, true));
        StoredToken t = TokenFile.Read(path, new FakeProtector());
        Assert.Equal("ghp_fromPowerShell", t.Token);
        Assert.Null(t.Expires);
    }

    [Fact]
    public void Unreadable_token_warns_and_continues_without_one()
    {
        using var dir = new TempDir();
        string path = dir.Write("wintainium-token.xml", "<Objs><broken");
        var log = new MemoryLog();
        var store = new TokenStore(path, new FakeProtector(), () => null);
        Assert.Null(store.ReadSaved(log));
        Assert.Contains("Click Setup to save the token again", log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public void Token_from_another_user_or_PC_is_not_readable()
    {
        using var dir = new TempDir();
        string path = dir.File("t.xml");
        TokenFile.Write(path, new StoredToken("ghp_x", null), new FakeProtector());
        string xml = File.ReadAllText(path, Encoding.Unicode).Replace("<SS N=\"Token\">d9", "<SS N=\"Token\">00", StringComparison.Ordinal);
        File.WriteAllText(path, xml, Encoding.Unicode);
        Assert.Throws<TokenFileException>(() => TokenFile.Read(path, new FakeProtector()));
    }

    [Fact]
    public async Task GITHUB_TOKEN_is_used_only_when_it_has_no_scopes()
    {
        using var dir = new TempDir();
        string path = dir.File("wintainium-token.xml");
        TokenFile.Write(path, new StoredToken("ghp_saved", null), new FakeProtector());
        using HttpClient client = GitHub().Client();

        var log = new MemoryLog();
        string? scoped = await new TokenStore(path, new FakeProtector(), () => "ghp_scoped").GetLookupTokenAsync(client, log);
        Assert.Equal("ghp_saved", scoped);
        Assert.Contains("GITHUB_TOKEN", log.AllText, StringComparison.Ordinal);

        string? clean = await new TokenStore(path, new FakeProtector(), () => "ghp_good").GetLookupTokenAsync(client, NullLog.Instance);
        Assert.Equal("ghp_good", clean);
    }

    [WindowsFact]
    public void Real_DPAPI_round_trip()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        string path = dir.File("wintainium-token.xml");
        TokenFile.Write(path, new StoredToken("ghp_dpapi", null), new DpapiProtector());
        Assert.Equal("ghp_dpapi", TokenFile.Read(path, new DpapiProtector()).Token);
    }

    [WindowsFact]
    public void Reads_a_token_saved_by_Windows_PowerShell()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        string path = dir.File("wintainium-token.xml");
        // exactly what v0.3.0's Save-GitHubToken does
        string script =
            "$sec = ConvertTo-SecureString 'ghp_fromPS51' -AsPlainText -Force; " +
            "[pscustomobject]@{ Token = $sec; Expires = [datetime]'2027-11-18T10:00:00' } | Export-Clixml -LiteralPath '" + path.Replace("'", "''", StringComparison.Ordinal) + "'";
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        using (Process p = Process.Start(psi)!)
        {
            p.WaitForExit(60000);
            Assert.Equal(0, p.ExitCode);
        }

        StoredToken t = TokenFile.Read(path, new DpapiProtector());
        Assert.Equal("ghp_fromPS51", t.Token);
        Assert.Equal(new DateTime(2027, 11, 18, 10, 0, 0), t.Expires);
    }
}
