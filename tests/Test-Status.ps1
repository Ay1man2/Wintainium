# End-to-end test of status detection and the release cache, with mocked release servers.
param([string]$Script = '')
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)
if (-not $Script) { $Script = Join-Path $repo 'Wintainium.ps1' }
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ("wintest-" + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $root | Out-Null
$work = Join-Path $root 'scripts'; New-Item -ItemType Directory $work | Out-Null
Copy-Item $Script (Join-Path $work 'Wintainium.ps1')
$fails = 0
function Check($name, $cond) { if ($cond) { "PASS  $name" } else { "FAIL  $name"; $script:fails++ } }

# --- fake installed apps --------------------------------------------------
function New-FakeExe($rel, [datetime]$when) {
    $p = Join-Path $root $rel; New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null
    Set-Content $p 'x'; (Get-Item $p).LastWriteTime = $when; return $p
}
$old = (Get-Date).AddDays(-60); $new = (Get-Date)
$cfg = @(
  @{ name='GhOld';    exe=(New-FakeExe 'a/ghold.exe' $old);  source='github'; repo='o/ghold';  update=@('download','page') },
  @{ name='GhNew';    exe=(New-FakeExe 'a/ghnew.exe' $new);  source='github'; repo='o/ghnew';  update=@('page') },
  @{ name='Rolling';  exe=(New-FakeExe 'a/roll.exe'  $old);  source='github'; repo='o/roll'; tag='latest'; graceDays=30; update=@('page') },
  @{ name='Pre';      exe=(New-FakeExe 'a/pre.exe'   $old);  source='github'; repo='o/pre'; prerelease=$true; update=@('page') },
  @{ name='Forgejo';  exe=(New-FakeExe 'a/fj.exe'    $new);  source='gitea';  api='https://git.example/api/v1/repos/x/fj/releases/latest'; update=@('page') },
  @{ name='Lab';      exe=(New-FakeExe 'a/lab.exe'   $old);  source='gitlab'; api='https://gitlab.com/api/v4/projects/x%2Flab/releases/permalink/latest'; update=@('page') },
  @{ name='Dol';      exe=(New-FakeExe 'a/dol.exe'   $new);  source='dolphin'; api='https://dolphin-emu.org/update/latest/beta/'; update=@('page') },
  @{ name='Manual';   exe=(New-FakeExe 'a/man.exe'   $new);  source='manual'; url='https://x'; update=@('page') },
  @{ name='Missing';  exe=(Join-Path $root 'a/nope.exe');    source='github'; repo='o/ghnew'; update=@('page') },
  @{ name='Broken';   exe=(New-FakeExe 'a/brk.exe'   $new);  source='github'; repo='o/broken'; update=@('page') },
  @{ name='Limited';  exe=(New-FakeExe 'a/lim.exe'   $new);  source='github'; repo='o/limited'; update=@('page') }
)
# Wintainium's own entry: the script knows its version, so a 60-day-old file is still current
$self = Join-Path $work 'Wintainium.ps1'; (Get-Item $self).LastWriteTime = $old
$selfVer = [regex]::Match((Get-Content -Raw $self), "AppVersion = '([\d.]+)'").Groups[1].Value
$cfg += @{ name='Self'; exe=$self; source='github'; repo='o/self'; update=@('page') }
$cfg += @{ name='SelfOld'; exe=$self; source='github'; repo='o/selfnext'; update=@('page') }
$cfg | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $work 'wintainium-apps.json')

# --- mocked network --------------------------------------------------------
$global:calls = [System.Collections.Generic.List[string]]::new()
function global:Invoke-RestMethod {
    param([Parameter(Position=0)]$Uri, $Headers, $Method)
    $global:calls.Add([string]$Uri)
    $d = (Get-Date).ToUniversalTime().ToString('o')
    switch -Regex ([string]$Uri) {
        'o/ghold/releases/latest' { return [pscustomobject]@{ tag_name='v2.0'; published_at=$d; html_url='https://gh/ghold'; body='notes'; assets=@([pscustomobject]@{name='ghold-2.0-win64.zip'; browser_download_url='https://dl/ghold-2.0-win64.zip'; updated_at=$d}) } }
        'o/self/releases/latest' { return [pscustomobject]@{ tag_name="v$selfVer"; published_at=$d; html_url='https://gh/self'; assets=@() } }
        'o/selfnext/releases/latest' { return [pscustomobject]@{ tag_name='v9.0.0'; published_at=(Get-Date).AddDays(-1).ToUniversalTime().ToString('o'); html_url='https://gh/selfnext'; assets=@() } }
        'o/ghnew/releases/latest' { return [pscustomobject]@{ tag_name='v1.0'; published_at=(Get-Date).AddDays(-10).ToUniversalTime().ToString('o'); html_url='https://gh/ghnew'; assets=@() } }
        'o/roll/releases/tags/latest' { return [pscustomobject]@{ tag_name='latest'; published_at='2020-01-01T00:00:00Z'; html_url='https://gh/roll'; assets=@([pscustomobject]@{name='r.zip'; browser_download_url='https://dl/r.zip'; updated_at=$d}) } }
        'o/pre/releases\?per_page' { return @([pscustomobject]@{ tag_name='nightly-5'; draft=$false; prerelease=$true; published_at=$d; html_url='https://gh/pre'; assets=@() }) }
        'git\.example' { return [pscustomobject]@{ tag_name='v3.0'; published_at=(Get-Date).AddDays(-1).ToUniversalTime().ToString('o'); html_url='https://fj/rel'; assets=@() } }
        'gitlab\.com' { return [pscustomobject]@{ tag_name='v9.9'; released_at=$d; _links=[pscustomobject]@{self='https://gl/rel'}; assets=[pscustomobject]@{ links=@([pscustomobject]@{name='Lab_9.9-x64.exe'; url='https://gl/dl/1'}) } } }
        'dolphin-emu' { return [pscustomobject]@{ shortrev='2609b'; date=(Get-Date).AddDays(-1).ToUniversalTime().ToString('o'); artifacts=@([pscustomobject]@{system='Windows x64'; url='https://dl.dolphin/dolphin-2609b-x64.7z'}) } }
        'o/broken' { throw 'Not Found (404)' }
        'o/limited' { throw 'Response status code does not indicate success: 403 (rate limit exceeded)' }
        default { throw "unexpected URI $Uri" }
    }
}

# --- run 1: list ----------------------------------------------------------
Push-Location $work
$out = & (Join-Path $work 'Wintainium.ps1') list -DownloadDir (Join-Path $root 'dl') -InstallRoot (Join-Path $root 'apps') *>&1 | Out-String
Pop-Location
function StatusOf($name) { ($out -split "`n" | Where-Object { $_ -match "^\s*\d+\s+$([regex]::Escape($name))\s" } | Select-Object -First 1) }
Check 'GhOld flagged UPDATE'              ((StatusOf 'GhOld') -match 'UPDATE')
Check 'GhNew up to date'                  ((StatusOf 'GhNew') -match 'up to date')
Check 'Rolling tag uses asset date, UPDATE past 30d grace' ((StatusOf 'Rolling') -match 'UPDATE')
Check 'Pre-release tracked'               ((StatusOf 'Pre') -match 'nightly-5')
Check 'Forgejo/Gitea source resolves'     ((StatusOf 'Forgejo') -match 'v3.0.*up to date|up to date.*v3.0')
Check 'GitLab source resolves + UPDATE'   ((StatusOf 'Lab') -match 'UPDATE' -and (StatusOf 'Lab') -match 'v9.9')
Check 'Dolphin feed resolves'             ((StatusOf 'Dol') -match '2609b')
Check 'Manual shows check manually'       ((StatusOf 'Manual') -match 'check manually')
Check 'Missing exe reported'              ((StatusOf 'Missing') -match 'exe not found')
Check '404 shows lookup failed'           ((StatusOf 'Broken') -match 'lookup failed')
Check '403 shows rate limited'            ((StatusOf 'Limited') -match 'rate limited')
Check 'Wintainium reports its own version, up to date' ((StatusOf 'Self') -match [regex]::Escape($selfVer) -and (StatusOf 'Self') -match 'up to date')
Check 'Wintainium flags its own newer release'  ((StatusOf 'SelfOld') -match 'UPDATE')
Check 'cache file written'                (Test-Path (Join-Path $work 'wintainium-cache.json'))
$n1 = $global:calls.Count

# --- run 2: cache hit ------------------------------------------------------
Push-Location $work
$out2 = & (Join-Path $work 'Wintainium.ps1') list -DownloadDir (Join-Path $root 'dl') *>&1 | Out-String
Pop-Location
$n2 = $global:calls.Count - $n1
Check "second run served from cache (made $n2 calls, only failed ones retried)" ($n2 -le 2)

"`n$fails failure(s)"
if ($env:SHOW) { $out }
Remove-Item -Recurse -Force $root
