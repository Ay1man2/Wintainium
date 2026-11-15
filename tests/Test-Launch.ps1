# Starts Wintainium.ps1 the way the desktop shortcut does (-File, from another folder)
# and checks it finds its own folder. Uses an empty app list, so nothing touches the network.
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)
$fails = 0
function Check($n, $c) { if ($c) { "PASS  $n" } else { "FAIL  $n"; $script:fails++ } }
$work = Join-Path ([IO.Path]::GetTempPath()) ("wintest-launch-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
Copy-Item (Join-Path $repo 'Wintainium.ps1') $work
$exe = (Get-Process -Id $PID).Path
Push-Location ([IO.Path]::GetTempPath())
try {
    # first start: no app list yet, so one is created next to the script
    $out = & $exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $work 'Wintainium.ps1') list 2>&1 | Out-String
} finally { Pop-Location }
Check 'starts with -File from another folder'   ($out -notmatch 'Cannot bind argument|Exception|error' -and $out -match 'Wintainium \d+\.\d+\.\d+')
Check 'app list created next to the script'     (Test-Path (Join-Path $work 'wintainium-apps.json'))
Check 'empty list shows 0 updates'              ($out -match '0 update\(s\) available')
Remove-Item -Recurse -Force $work
"`n$fails failure(s)"
