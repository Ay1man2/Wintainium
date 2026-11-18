#Requires -Version 5.1
<#
.SYNOPSIS
  Builds the release download: dist\Wintainium-<version>.zip plus a .sha256 file.

  The zip holds Wintainium.exe (a small launcher, compiled here from launcher\WintainiumLauncher.cs
  with the C# compiler that ships with Windows, so no SDK or download is needed), the scripts,
  the list of recognized apps, the example app list, the docs and the icon. Upload both files to the GitHub release.

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
$zip = Join-Path $dist "Wintainium-$ver.zip"
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'assets') | Out-Null

# launcher: stamp the version, then compile
$cs = (Get-Content (Join-Path $root 'launcher\WintainiumLauncher.cs') -Raw) -replace '"0\.0\.0\.0"', "`"$ver.0`""
$tmp = Join-Path ([IO.Path]::GetTempPath()) "WintainiumLauncher-$ver.cs"
Set-Content -LiteralPath $tmp -Value $cs -Encoding UTF8
& $csc /nologo /target:winexe /optimize+ /platform:anycpu "/out:$(Join-Path $stage 'Wintainium.exe')" "/win32icon:$(Join-Path $root 'assets\icon.ico')" /reference:System.Windows.Forms.dll $tmp
if ($LASTEXITCODE) { throw 'Compiling Wintainium.exe failed.' }
Remove-Item -LiteralPath $tmp -Force

foreach ($f in 'Wintainium.ps1', 'Find-Sources.ps1', 'known-apps.json', 'wintainium-apps.example.json', 'README.md', 'CHANGELOG.md', 'CONTRIBUTING.md', 'LICENSE') {
    Copy-Item -LiteralPath (Join-Path $root $f) -Destination $stage
}
foreach ($f in 'icon.ico', 'icon-256.png') {
    Copy-Item -LiteralPath (Join-Path $root "assets\$f") -Destination (Join-Path $stage 'assets')
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLower()
Set-Content -LiteralPath "$zip.sha256" -Value "$hash  Wintainium-$ver.zip" -Encoding ASCII

Write-Host ''
Write-Host "Built $zip" -ForegroundColor Green
Write-Host "SHA-256 $hash"
Write-Host ''
Write-Host 'Next: try it (unzip dist\*.zip somewhere else and double-click Wintainium.exe), then attach'
Write-Host "Wintainium-$ver.zip and Wintainium-$ver.zip.sha256 to the GitHub release."
