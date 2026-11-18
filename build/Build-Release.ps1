#Requires -Version 5.1
<#
.SYNOPSIS
  Builds the release downloads in dist\:

    Wintainium.exe                     the one-file download: double-click it and it installs itself
                                       for the current user (it carries the scripts listed below)
    Wintainium-<version>-portable.zip  the same files unpacked, for running from any folder
    *.sha256                           a checksum for each, which Wintainium checks when it updates itself

  Wintainium.exe is compiled here from launcher\WintainiumLauncher.cs with the C# compiler that
  ships with Windows, so no SDK or download is needed. Attach all four files to the GitHub release.

    .\build\Build-Release.ps1             # runs the tests first, then builds
    .\build\Build-Release.ps1 -SkipTests
#>
[CmdletBinding()]
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$main = Get-Content (Join-Path $root 'Wintainium.ps1') -Raw
$m = [regex]::Match($main, "\`$script:AppVersion = '([\d.]+)'")
if (-not $m.Success) { throw 'Could not read $script:AppVersion from Wintainium.ps1.' }
$ver = $m.Groups[1].Value
Write-Host "Building Wintainium $ver" -ForegroundColor Cyan

if (-not $SkipTests) {
    & (Join-Path $root 'tests\Run-Tests.ps1')
    if ($LASTEXITCODE) { throw 'Tests failed. Fix them before releasing (or use -SkipTests).' }
}

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $csc)) { throw 'The .NET Framework 4 C# compiler (csc.exe) was not found.' }

$dist = Join-Path $root 'dist'
$stage = Join-Path $dist "Wintainium-$ver"
$zipName = "Wintainium-$ver-portable.zip"
$zip = Join-Path $dist $zipName
$exe = Join-Path $dist 'Wintainium.exe'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Get-ChildItem -LiteralPath $dist -File | Where-Object { $_.Name -like 'Wintainium*' } | Remove-Item -Force
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'assets') | Out-Null

# The files Wintainium needs to run. The exe carries them; the portable zip has them unpacked.
$payload = @('Wintainium.ps1', 'Find-Sources.ps1', 'known-apps.json', 'wintainium-apps.example.json', 'LICENSE', 'assets\icon.ico')
$resources = foreach ($f in $payload) {
    $full = Join-Path $root $f
    if (-not (Test-Path -LiteralPath $full)) { throw "Missing $f" }
    "/resource:$full,payload/$($f.Replace('\', '/'))"
}

# launcher: stamp the version, then compile with the scripts inside
$cs = (Get-Content (Join-Path $root 'launcher\WintainiumLauncher.cs') -Raw) -replace '"0\.0\.0\.0"', "`"$ver.0`""
$tmp = Join-Path ([IO.Path]::GetTempPath()) "WintainiumLauncher-$ver.cs"
Set-Content -LiteralPath $tmp -Value $cs -Encoding UTF8
& $csc /nologo /target:winexe /optimize+ /platform:anycpu "/out:$exe" "/win32icon:$(Join-Path $root 'assets\icon.ico')" /reference:System.Windows.Forms.dll @resources $tmp
if ($LASTEXITCODE) { throw 'Compiling Wintainium.exe failed.' }
Remove-Item -LiteralPath $tmp -Force

# portable zip: the same exe next to the unpacked files (it runs them in place and installs nothing)
Copy-Item -LiteralPath $exe -Destination $stage
foreach ($f in $payload + @('README.md', 'CHANGELOG.md', 'CONTRIBUTING.md')) {
    Copy-Item -LiteralPath (Join-Path $root $f) -Destination (Join-Path $stage $f)
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)

foreach ($f in $exe, $zip) {
    $hash = (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLower()
    $name = Split-Path -Leaf $f
    Set-Content -LiteralPath "$f.sha256" -Value "$hash  $name" -Encoding ASCII
    Write-Host ("{0,-34} SHA-256 {1}" -f $name, $hash)
}

Write-Host ''
Write-Host "Built Wintainium $ver in $dist" -ForegroundColor Green
Write-Host 'Next: try dist\Wintainium.exe on its own (copy it somewhere else first), then attach these four'
Write-Host "files to the GitHub release: Wintainium.exe, Wintainium.exe.sha256, $zipName, $zipName.sha256"
