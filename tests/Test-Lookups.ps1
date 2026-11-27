# Failed lookups: GitHub-only token/limit statuses, other sites "site busy", cached fallback,
# itch.io request headers, GitHub token checks and the settings file. Network is mocked.
param([string]$Script = '')
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)
if (-not $Script) { $Script = Join-Path $repo 'Wintainium.ps1' }
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ("wintest-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $root | Out-Null
$work = Join-Path $root 'scripts'; New-Item -ItemType Directory $work | Out-Null
Copy-Item $Script (Join-Path $work 'Wintainium.ps1')
$fails = 0
function Check($name, $cond) { if ($cond) { "PASS  $name" } else { "FAIL  $name"; $script:fails++ } }

function New-FakeExe($rel) {
    $p = Join-Path $root $rel; New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null
    Set-Content $p 'x'; return $p
}
$cfg = @(
    @{ name = 'ItchCached';  exe = (New-FakeExe 'a/ic.exe'); source = 'itch'; url = 'https://dev.itch.io/cached'; update = @('page') },
    @{ name = 'ItchNew';     exe = (New-FakeExe 'a/in.exe'); source = 'itch'; url = 'https://dev.itch.io/fresh'; update = @('page') },
    @{ name = 'ForgeDown';   exe = (New-FakeExe 'a/fd.exe'); source = 'gitea'; api = 'https://codeberg.org/api/v1/repos/x/y/releases/latest'; update = @('page') },
    @{ name = 'GhBadToken';  exe = (New-FakeExe 'a/gb.exe'); source = 'github'; repo = 'o/badtoken'; update = @('page') },
    @{ name = 'GhOk';        exe = (New-FakeExe 'a/go.exe'); source = 'github'; repo = 'o/ok'; update = @('page') },
    @{ name = 'HttpForge';   exe = (New-FakeExe 'a/hf.exe'); source = 'gitea'; api = 'http://git.example/api/v1/repos/x/y/releases/latest'; update = @('page') }
)
$cfg | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $work 'wintainium-apps.json')

# ItchCached already has an answer from earlier (older than the cache window)
$old = (Get-Date).ToUniversalTime().AddHours(-5).ToString('o')
@{ ItchCached = @{ Tag = '2.1.0'; Date = (Get-Date).AddDays(-30).ToUniversalTime().ToString('o'); Page = 'https://dev.itch.io/cached'; Assets = @(); Notes = ''; Fetched = $old } } |
    ConvertTo-Json -Depth 5 | Set-Content (Join-Path $work 'wintainium-cache.json')

$global:itchHeadersSeen = $null
function global:Invoke-WebRequest {
    param([Parameter(Position = 0)]$Uri, $Headers, [switch]$UseBasicParsing, $TimeoutSec)
    if ([string]$Uri -match 'itch\.io') {
        $global:itchHeadersSeen = $Headers
        throw 'Response status code does not indicate success: 403 (Forbidden).'
    }
    throw "unexpected URI $Uri"
}
function global:Invoke-RestMethod {
    param([Parameter(Position = 0)]$Uri, $Headers, $Method, $TimeoutSec)
    $global:restSeen += ,[string]$Uri
    $d = (Get-Date).AddDays(-40).ToUniversalTime().ToString('o')
    switch -Regex ([string]$Uri) {
        'codeberg' { throw 'Response status code does not indicate success: 503 (Service Unavailable).' }
        'o/badtoken' { throw 'Response status code does not indicate success: 401 (Unauthorized). Bad credentials' }
        'o/ok/releases/latest' { return [pscustomobject]@{ tag_name = 'v1.0'; published_at = $d; html_url = 'https://gh/ok'; assets = @() } }
        default { throw "unexpected URI $Uri" }
    }
}

$global:restSeen = @()
Push-Location $work
$out =& (Join-Path $work 'Wintainium.ps1') list -DownloadDir (Join-Path $root 'dl') *>&1 | Out-String
Pop-Location
function StatusOf($name) { ($out -split "`n" | Where-Object { $_ -match "^\s*\d+\s+$([regex]::Escape($name))\s" } | Select-Object -First 1) }

Check 'itch.io 403 without a saved answer is "site busy", not a GitHub status' ((StatusOf 'ItchNew') -match 'site busy')
Check 'itch.io 403 with a saved answer falls back to it'  ((StatusOf 'ItchCached') -match 'up to date' -and (StatusOf 'ItchCached') -match '2\.1\.0 \(cached\)')
Check 'Codeberg 503 is "site busy"'                       ((StatusOf 'ForgeDown') -match 'site busy')
Check 'GitHub 401 is "token rejected"'                    ((StatusOf 'GhBadToken') -match 'token rejected')
Check 'GitHub lookups still work'                         ((StatusOf 'GhOk') -match 'up to date')
Check 'token message shown once for the GitHub app'       (([regex]::Matches($out, 'GitHub rejected the saved token')).Count -eq 1)
Check 'no rate-limit message for an itch.io failure'      ($out -notmatch 'hourly lookup limit')
Check 'site-busy note names itch.io'                      ($out -match "ItchNew: itch\.io didn't answer")
Check 'fallback note says when the saved answer is from'  ($out -match 'ItchCached: itch\.io didn''t answer, so this shows what it reported at')
Check 'itch.io request looks like a browser and identifies Wintainium' ($global:itchHeadersSeen -and $global:itchHeadersSeen['User-Agent'] -match '^Mozilla/5\.0 .*Wintainium/' -and $global:itchHeadersSeen['Accept'] -match 'text/html')
Check 'itch.io request never carries the GitHub token'    ($global:itchHeadersSeen -and -not $global:itchHeadersSeen.ContainsKey('Authorization'))
Check 'http release address is not requested'             (-not (@($global:restSeen) -match '^http://'))
Check 'http release address shows "lookup failed"'        ((StatusOf 'HttpForge') -match 'lookup failed')
Check 'http release address explained'                    ($out -match 'HttpForge: Wintainium only looks up releases over https')

# --- token checks (functions loaded on their own) -----------------------------
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $work 'Wintainium.ps1'), [ref]$null, [ref]$null)
foreach ($f in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -in 'Get-HeaderValue', 'ConvertFrom-GitHubExpiry', 'Test-GitHubToken' }, $true)) {
    Invoke-Expression ($f.Extent.Text -replace '^function\s+', 'function global:')
}
$e1 = ConvertFrom-GitHubExpiry '2027-01-14 13:46:22 -0500'
Check 'expiry with offset parsed' ($e1 -and $e1.ToUniversalTime().ToString('yyyy-MM-dd HH:mm') -eq '2027-01-14 18:46')
$e2 = ConvertFrom-GitHubExpiry '2027-01-14 18:46:22 UTC'
Check 'expiry in UTC parsed' ($e2 -and $e2.ToUniversalTime().ToString('HH:mm') -eq '18:46')

$global:mockScopes = ''
$global:mockExpiry = '2027-11-18 10:00:00 -0500'
function global:Invoke-WebRequest {
    param([Parameter(Position = 0)]$Uri, $Headers, [switch]$UseBasicParsing)
    if ($Headers['Authorization'] -notin 'Bearer ghp_good', 'Bearer ghp_scoped', 'Bearer github_pat_fine') { throw '401 Bad credentials' }
    $h = @{ 'X-OAuth-Scopes' = $(if ($Headers['Authorization'] -eq 'Bearer ghp_scoped') { 'repo, workflow' } else { '' }); 'GitHub-Authentication-Token-Expiration' = $global:mockExpiry }
    return [pscustomobject]@{ Content = '{"rate":{"limit":5000,"remaining":4999}}'; Headers = $h }
}
$t = Test-GitHubToken '  ghp_good  '
Check 'no-scope token accepted and marked safe' ($t.Ok -and $t.Safe -and $t.Limit -eq 5000 -and $t.Expires.Year -eq 2027)
$t = Test-GitHubToken 'ghp_scoped'
Check 'token with scopes is flagged, not safe' ($t.Ok -and -not $t.Safe -and $t.Scopes -eq 'repo, workflow' -and $t.Message -match 'only needs a token with none')
$t = Test-GitHubToken 'github_pat_fine'
Check 'fine-grained token not treated as safe (permissions unknowable)' ($t.Ok -and -not $t.Safe -and $t.Fine -and $t.Message -match 'classic token')
$t = Test-GitHubToken 'ghp_wrong'
Check 'rejected token reported' (-not $t.Ok -and $t.Message -match 'did not accept')
$t = Test-GitHubToken ''
Check 'empty token reported' (-not $t.Ok)

# --- settings file is used when no option is given ----------------------------
@{ installRoot = (Join-Path $root 'MyApps'); downloadDir = (Join-Path $root 'MyDownloads'); setupDone = $true } | ConvertTo-Json | Set-Content (Join-Path $work 'wintainium-settings.json')
$probe = Join-Path $work 'probe.ps1'
$src = Get-Content (Join-Path $work 'Wintainium.ps1') -Raw
$cut = $src.IndexOf('$storedToken = Get-StoredToken')
Set-Content $probe ($src.Substring(0, $cut) + "`n`"ROOT=`$InstallRoot`"; `"DL=`$DownloadDir`"")
$p = & $probe list *>&1 | Out-String
Check 'install folder from settings file' ($p -match [regex]::Escape("ROOT=$(Join-Path $root 'MyApps')"))
Check 'download folder from settings file' ($p -match [regex]::Escape("DL=$(Join-Path $root 'MyDownloads')"))
$p = & $probe list -InstallRoot 'X:\Override' *>&1 | Out-String
Check 'command-line option still wins' ($p -match 'ROOT=X:\\Override')

Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
"`n$fails failure(s)"
