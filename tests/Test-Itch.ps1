$ErrorActionPreference = 'Stop'
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)) 'Wintainium.ps1'), [ref]$null, [ref]$null)
$ast.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.FunctionDefinitionAst] } | ForEach-Object { . ([scriptblock]::Create($_.Extent.Text)) }
$fails = 0
function Check($n, $c) { if ($c) { "PASS  $n" } else { "FAIL  $n"; $script:fails++ } }
$real = @'
<div class="upload"><div class="info_column"><div class="upload_name"><strong title="some-game-windows.zip" class="name">some-game-windows.zip</strong> <span class="file_size"><span>308 MB</span></span> <span class="download_platforms"><span aria-hidden="true" class="icon icon-windows8" title="Download for Windows"></span> </span></div><div class="build_row"><span class="version_name">Version a8c30565</span> <span class="version_date"><abbr title="12 October 2026 @ 19:03 UTC"><span aria-hidden="true" class="icon icon-stopwatch"></span> 34 days ago</abbr></span></div></div></div></div></div><section id="devlog" class="game_devlog">
'@
$r = Read-ItchPage $real 'https://example.itch.io/some-game' ''
Check 'itch: version read'          ($r.Tag -eq 'a8c30565')
Check 'itch: exact UTC date read'   ($r.Date -eq [datetime]::new(2026,10,12,19,3,0,[DateTimeKind]::Utc))
Check 'itch: page kept as link'     ($r.Page -eq 'https://example.itch.io/some-game')
$two = '<strong class="name">game-linux.tar.gz</strong><span class="icon icon-tux"></span><span class="version_name">Version L1</span><abbr title="1 November 2026 @ 9:05 UTC">x</abbr>' + $real
$r2 = Read-ItchPage $two 'p' ''
Check 'itch: prefers the Windows upload' ($r2.Tag -eq 'a8c30565')
$r3 = Read-ItchPage $two 'p' 'linux'
Check 'itch: asset pattern overrides'    ($r3.Tag -eq 'L1' -and $r3.Date.Hour -eq 9)
$noVer = '<strong class="name">plain.zip</strong><span class="icon icon-windows8"></span><abbr title="3 March 2026 @ 10:00 UTC">x</abbr>'
Check 'itch: no version -> file name'    ((Read-ItchPage $noVer 'p' '').Tag -eq 'plain.zip')
try { Read-ItchPage '<html>nothing</html>' 'p' ''; Check 'itch: empty page throws' $false } catch { Check 'itch: empty page throws (shown as lookup failed)' $true }
$Config = Join-Path ([IO.Path]::GetTempPath()) 'itch-apps.json'; '[]' | Set-Content $Config; $script:apps = @()
$global:q = [System.Collections.Generic.Queue[string]]::new()
'https://example.itch.io/some-game','Some Game','C:\Games\SomeGame\SomeGame.exe' | ForEach-Object { $global:q.Enqueue($_) }
function global:Read-Host { param($Prompt) $global:q.Dequeue() }
Add-App 6>&1 | Out-Null
# Windows PowerShell 5.1 returns a JSON array as one object; unroll it the way Wintainium does
$e = @((Get-Content $Config -Raw | ConvertFrom-Json) | ForEach-Object { $_ })[0]
Check 'add-app: itch URL -> itch source' ($e.source -eq 'itch' -and $e.url -eq 'https://example.itch.io/some-game' -and $e.name -eq 'Some Game' -and @($e.update)[0] -eq 'page')
Remove-Item $Config
"`n$fails failure(s)"
