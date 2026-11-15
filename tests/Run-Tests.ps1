<#
.SYNOPSIS
  Runs Wintainium's test suites under every PowerShell found on this machine
  (Windows PowerShell 5.1 and PowerShell 7 on Windows; PowerShell 7 elsewhere).
  The suites mock the network and Windows-only parts. The WPF window is not tested here.
#>
$here = Split-Path -Parent $MyInvocation.MyCommand.Definition
$root = Split-Path -Parent $here
$failed = 0
foreach ($f in 'Wintainium.ps1', 'Find-Sources.ps1') {
    $tokens = $null; $errs = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root $f), [ref]$tokens, [ref]$errs)
    if ($errs.Count) { $failed++; $errs | ForEach-Object { "FAIL  $f line $($_.Extent.StartLineNumber): $($_.Message)" } } else { "PASS  $f parses" }
}

$hosts = @()
foreach ($h in 'powershell.exe', 'pwsh') {
    $cmd = Get-Command $h -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { $hosts += $cmd.Source }
}
if (-not $hosts) { $hosts = @((Get-Process -Id $PID).Path) }

foreach ($exe in $hosts) {
    $ver = & $exe -NoProfile -Command '$PSVersionTable.PSVersion.ToString()'
    "`n######## PowerShell $ver ($exe)"
    foreach ($t in Get-ChildItem $here -Filter 'Test-*.ps1') {
        "`n== $($t.BaseName)"
        $out = & $exe -NoProfile -ExecutionPolicy Bypass -File $t.FullName 2>&1 | Out-String
        $out.Trim()
        if ($out -notmatch '(?m)^\s*0 failure\(s\)') { $failed++ }
    }
}
"`n" + $(if ($failed) { "$failed suite run(s) failed" } else { 'All suites passed' })
exit $failed
