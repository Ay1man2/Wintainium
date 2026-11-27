#Requires -Version 5.1
<#
.SYNOPSIS
  Builds the release downloads in dist\:

    Wintainium.exe                     the one-file download: double-click it and it installs itself
                                       for the current user
    Wintainium-<version>-portable.zip  the same exe with portable.txt, for running from any folder
                                       without installing anything
    *.sha256                           a checksum for each, which Wintainium checks when it updates itself

  Needs the .NET SDK named in global.json. The release workflow runs this same script on a tag
  and puts the four files on a draft release.

    .\build\Build-Release.ps1             # runs the tests first, then builds
    .\build\Build-Release.ps1 -SkipTests
#>
[CmdletBinding()]
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$ver = [string]$props.Project.PropertyGroup.Version
if ($ver -notmatch '^\d+\.\d+\.\d+$') { throw 'Could not read <Version> from Directory.Build.props.' }
Write-Host "Building Wintainium $ver" -ForegroundColor Cyan

Push-Location $root
try {
    if (-not $SkipTests) {
        dotnet test Wintainium.sln -c Release
        if ($LASTEXITCODE) { throw 'Tests failed. Fix them before releasing (or use -SkipTests).' }
    }

    $dist = Join-Path $root 'dist'
    $publish = Join-Path $dist 'publish'
    $stage = Join-Path $dist "Wintainium-$ver"
    $zipName = "Wintainium-$ver-portable.zip"
    $zip = Join-Path $dist $zipName
    $exe = Join-Path $dist 'Wintainium.exe'
    foreach ($d in $publish, $stage) { if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force } }
    New-Item -ItemType Directory -Force -Path $dist, $stage | Out-Null
    Get-ChildItem -LiteralPath $dist -File | Where-Object { $_.Name -like 'Wintainium*' } | Remove-Item -Force

    # one self-contained file: no .NET needs to be installed on the PC
    dotnet publish src/Wintainium.App/Wintainium.App.csproj -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -o $publish
    if ($LASTEXITCODE) { throw 'Publishing Wintainium.exe failed.' }
    Copy-Item -LiteralPath (Join-Path $publish 'Wintainium.exe') -Destination $exe

    # portable zip: the same exe and portable.txt, which tells it to run in place and install nothing
    Copy-Item -LiteralPath $exe -Destination $stage
    Set-Content -LiteralPath (Join-Path $stage 'portable.txt') -Encoding ASCII -Value @(
        'This copy of Wintainium is portable: it runs from this folder and installs nothing.',
        'Your app list, settings and token are saved in this folder.',
        'Delete this file to have Wintainium.exe install itself for your Windows account instead.')
    foreach ($f in 'LICENSE', 'README.md', 'CHANGELOG.md') {
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
} finally {
    Pop-Location
}

Write-Host ''
Write-Host "Built Wintainium $ver in $dist" -ForegroundColor Green
Write-Host 'Next: try dist\Wintainium.exe on its own (copy it somewhere else first). The four files to release:'
Write-Host "Wintainium.exe, Wintainium.exe.sha256, $zipName, $zipName.sha256"
