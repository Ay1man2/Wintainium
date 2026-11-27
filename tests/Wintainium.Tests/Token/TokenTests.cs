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

    [Fact]
    public async Task Network_failure_is_not_blamed_on_the_token()
    {
        var http = new FakeHttp().OnThrow(GitHubTokenCheck.RateLimitUrl, new HttpRequestException("no route"));
        using HttpClient client = http.Client();
        TokenCheckResult t = await GitHubTokenCheck.CheckAsync(client, "ghp_good");
        Assert.False(t.Ok);
        Assert.Equal(GitHubTokenCheck.NetworkMessage, t.Message);

        var slow = new FakeHttp().OnThrow(GitHubTokenCheck.RateLimitUrl, new TaskCanceledException("timed out"));
        using HttpClient slowClient = slow.Client();
        Assert.Equal(GitHubTokenCheck.NetworkMessage, (await GitHubTokenCheck.CheckAsync(slowClient, "ghp_good")).Message);

        var down = new FakeHttp().OnStatus(GitHubTokenCheck.RateLimitUrl, HttpStatusCode.ServiceUnavailable);
        using HttpClient downClient = down.Client();
        TokenCheckResult d = await GitHubTokenCheck.CheckAsync(downClient, "ghp_good");
        Assert.False(d.Ok);
        Assert.DoesNotContain("did not accept", d.Message, StringComparison.Ordinal);
        Assert.Contains("503", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_the_check_is_passed_on()
    {
        using HttpClient client = GitHub().Client();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GitHubTokenCheck.CheckAsync(client, "ghp_good", cts.Token));
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

    [Fact]
    public async Task Expiry_is_cleared_when_no_token_is_used()
    {
        using var dir = new TempDir();
        string path = dir.File("wintainium-token.xml");
        TokenFile.Write(path, new StoredToken("ghp_saved", new DateTime(2027, 1, 1)), new FakeProtector());
        var store = new TokenStore(path, new FakeProtector(), () => null);
        Assert.NotNull(store.ReadSaved(NullLog.Instance));
        Assert.NotNull(store.Expires);

        File.Delete(path);
        using HttpClient client = GitHub().Client();
        Assert.Null(await store.GetLookupTokenAsync(client, NullLog.Instance));
        Assert.Null(store.Expires);
    }

    /// <summary>
    /// Reads a copy of the owner's real token file (saved by v0.3.0) with real DPAPI. Only checks
    /// that a token comes out; it never prints the token or sends it anywhere.
    /// </summary>
    [RealTokenFact]
    public void Reads_the_real_token_file_saved_by_v030()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string source = Environment.GetEnvironmentVariable(RealTokenFactAttribute.Variable)!.Trim().Trim('"');
        Assert.True(File.Exists(source), $"{RealTokenFactAttribute.Variable} names a file that doesn't exist: {source}");
        using var dir = new TempDir();
        string copy = dir.File("wintainium-token.xml");
        File.Copy(source, copy);

        var log = new MemoryLog();
        StoredToken? t = new TokenStore(copy, new DpapiProtector(), () => null).ReadSaved(log);
        Assert.True(t is not null, "The token file could not be read: " + log.AllText);
        Assert.True(t.Token.Length >= 20, "The token read back is too short to be a GitHub token.");
        Assert.Equal(t.Token.Trim(), t.Token);
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
            "$ErrorActionPreference = 'Stop'; " +
            "$sec = ConvertTo-SecureString 'ghp_fromPS51' -AsPlainText -Force; " +
            "[pscustomobject]@{ Token = $sec; Expires = [datetime]'2027-11-18T10:00:00' } | Export-Clixml -LiteralPath '" + path.Replace("'", "''", StringComparison.Ordinal) + "'";
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        // A PSModulePath inherited from a .NET or PowerShell 7 process makes Windows PowerShell
        // load the wrong Microsoft.PowerShell.Security module, so ConvertTo-SecureString fails.
        psi.Environment.Remove("PSModulePath");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        using (Process p = Process.Start(psi)!)
        {
            string errors = p.StandardError.ReadToEnd();
            p.WaitForExit(60000);
            Assert.True(p.ExitCode == 0 && errors.Length == 0, $"powershell.exe failed ({p.ExitCode}): {errors}");
        }

        string written = File.ReadAllText(path);
        StoredToken t;
        try
        {
            t = TokenFile.Read(path, new DpapiProtector());
        }
        catch (TokenFileException ex)
        {
            Assert.Fail($"{ex.Message}\nFile written by PowerShell:\n{written}");
            throw;
        }

        Assert.Equal("ghp_fromPS51", t.Token);
        Assert.Equal(new DateTime(2027, 11, 18, 10, 0, 0), t.Expires);
    }
}
