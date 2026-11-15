<#
.SYNOPSIS
  Read-only scan for everything on this PC that came from GitHub (or GitLab, Codeberg,
  Forgejo and similar), so nothing is missing from wintainium-apps.json.
  Nothing is changed, moved or deleted.

  It looks for four things:
    Download  files carrying Windows' "downloaded from" mark (Zone.Identifier), with the URL
    Repo      folders cloned with git, with their remote URL
    App       programs in your emulation folders that Wintainium is not tracking yet
    Mod       mod, texture, cheat and patch folders in your emulators, with where they came from

  Each row says whether Wintainium already tracks it. Results go to sources-inventory.csv on the Desktop.

.EXAMPLE
  .\Find-Sources.ps1
  .\Find-Sources.ps1 -ExtraRoots 'D:\Games', 'C:\Emulation\PC'
#>
[CmdletBinding()]
param(
    [string[]]$DownloadRoots = @(
        "$env:USERPROFILE\Downloads",
        "$env:USERPROFILE\Desktop",
        "$env:USERPROFILE\Documents",
        'C:\Emulation',
        "$env:LOCALAPPDATA\Programs"
    ),
    [string[]]$RepoRoots = @(
        $env:USERPROFILE,
        'C:\Emulation',
        "$env:APPDATA\EmuDeck"
    ),
    [string[]]$AppRoots = @(
        "$env:USERPROFILE\Wintainium\Apps",
        'C:\Emulation\tools',
        "$env:APPDATA\EmuDeck\Emulators"
    ),
    [string[]]$ModRoots = @(
        'C:\Emulation',
        "$env:APPDATA\EmuDeck\Emulators"
    ),
    # Your own folders (where you keep emulators, PC ports, synced files); added to every list above.
    [string[]]$ExtraRoots = @(),
    # Defaults to wintainium-apps.json next to this script.
    [string]$Config = '',
    [string]$OutCsv = "$env:USERPROFILE\Desktop\sources-inventory.csv"
)

# The script's own folder. Windows PowerShell 5.1 leaves $PSScriptRoot empty while the
# param() defaults are evaluated when started with -File (as the shortcut does), so resolve it here.
$script:Here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
if (-not $Config) { $Config = Join-Path $script:Here 'wintainium-apps.json' }

$DownloadRoots += $ExtraRoots; $RepoRoots += $ExtraRoots; $AppRoots += $ExtraRoots; $ModRoots += $ExtraRoots

# Folders never worth walking: game libraries, caches, package stores.
$skipDirs = '(?i)\\(roms|saves|states|screenshots|shadercache|shader_cache|cache|node_modules|\.git|downloaded_media|EmuUpdates)(\\|$)'
$fileTypes = '(?i)\.(zip|7z|rar|exe|msi|apk|appimage|tar|gz|xz|jar|nsp|bat)$'
$forgeHosts = '(?i)(^|\.)(github\.com|githubusercontent\.com|gitlab\.com|codeberg\.org|bitbucket\.org|sourceforge\.net|gitea\.com)$|^git\.'

# ---------------------------------------------------------------------------
# What Wintainium already tracks
# ---------------------------------------------------------------------------
$tracked = @()
if (Test-Path -LiteralPath $Config) {
    $loaded = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
    $tracked = @($loaded | ForEach-Object { $_ })
}
$trackedRepos = @($tracked | ForEach-Object {
    if ($_.repo) { ([string]$_.repo).ToLower() }
    if ($_.api -and [string]$_.api -match '/repos/([^/]+/[^/]+)/releases') { $Matches[1].ToLower() }
    if ($_.api -and [string]$_.api -match '/projects/([^/]+)/releases') { [uri]::UnescapeDataString($Matches[1]).ToLower() }
})
$trackedDirs = @($tracked | Where-Object { $_.exe } | ForEach-Object {
    (Split-Path -Path ([Environment]::ExpandEnvironmentVariables([string]$_.exe)) -Parent).TrimEnd('\').ToLower()
})

function Test-TrackedRepo([string]$repo) {
    if (-not $repo) { return $false }
    return $trackedRepos -contains $repo.ToLower()
}

function Test-TrackedDir([string]$dir) {
    $d = $dir.TrimEnd('\').ToLower()
    foreach ($t in $trackedDirs) { if ($d -eq $t -or $d.StartsWith("$t\") -or $t.StartsWith("$d\")) { return $true } }
    return $false
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
# GitHub download links carry a numeric repository ID instead of the name; the API turns it back.
$ghHeaders = @{ 'User-Agent' = 'Wintainium'; 'Accept' = 'application/vnd.github+json' }
$tok = $env:GITHUB_TOKEN
$tokFile = Join-Path $script:Here 'wintainium-token.xml'
if (-not $tok -and (Test-Path -LiteralPath $tokFile)) {
    try { $tok = (New-Object Net.NetworkCredential('', (Import-Clixml -LiteralPath $tokFile).Token)).Password } catch { $tok = $null }
}
if ($tok) { $ghHeaders['Authorization'] = "Bearer $tok" }
$repoIdCache = @{}
function Resolve-GitHubRepoId([string]$id) {
    if ($repoIdCache.ContainsKey($id)) { return $repoIdCache[$id] }
    $name = ''
    try { $name = [string](Invoke-RestMethod "https://api.github.com/repositories/$id" -Headers $ghHeaders -ErrorAction Stop).full_name } catch { $name = '' }
    $repoIdCache[$id] = $name
    return $name
}

function Get-Origin([string]$path) {
    # Returns @{ HostUrl; ReferrerUrl } from the file's Zone.Identifier stream, or $null.
    try { $z = Get-Content -LiteralPath $path -Stream Zone.Identifier -ErrorAction Stop } catch { return $null }
    $o = @{ HostUrl = ''; ReferrerUrl = '' }
    foreach ($line in $z) {
        if ($line -match '^HostUrl=(.*)$') { $o.HostUrl = $Matches[1].Trim() }
        if ($line -match '^ReferrerUrl=(.*)$') { $o.ReferrerUrl = $Matches[1].Trim() }
    }
    return $o
}

# Turns a URL into "owner/repo" plus the site, when it points at a code-hosting site.
function Get-RepoFromUrl([string]$url) {
    if (-not $url -or $url -notmatch '^https?://') { return $null }
    try { $u = [uri]$url } catch { return $null }
    $site = $u.Host.ToLower()
    $parts = @($u.AbsolutePath.Trim('/') -split '/' | Where-Object { $_ })
    if ($site -match 'githubusercontent\.com$') {
        # raw.githubusercontent.com/owner/repo/...; release-asset hosts carry no repo
        if ($site -like 'raw.*' -and $parts.Count -ge 2) { return @{ Site = 'github.com'; Repo = "$($parts[0])/$($parts[1])" } }
        if ($u.AbsolutePath -match '/github-production-release-asset(?:-[0-9a-f]+)?/(\d+)/') {
            return @{ Site = 'github.com'; Repo = (Resolve-GitHubRepoId $Matches[1]) }
        }
        return @{ Site = 'github.com'; Repo = '' }
    }
    if ($site -notmatch $forgeHosts -and $u.AbsolutePath -notmatch '/releases/(download|tag)/|/-/releases') { return $null }
    if ($parts.Count -lt 2) { return @{ Site = $site; Repo = '' } }
    if ($site -eq 'gitlab.com') {
        $cut = [array]::IndexOf($parts, '-')
        $p = if ($cut -gt 1) { $parts[0..($cut - 1)] } else { $parts[0..1] }
        return @{ Site = $site; Repo = ($p -join '/') }
    }
    return @{ Site = $site; Repo = "$($parts[0])/$($parts[1])" }
}

function Get-Files([string]$root, [int]$depth, [string]$pattern) {
    if (-not $root -or -not (Test-Path -LiteralPath $root)) { return }
    Get-ChildItem -LiteralPath $root -File -Recurse -Depth $depth -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch $skipDirs -and $_.Name -match $pattern }
}

$rows = New-Object System.Collections.Generic.List[object]
function Add-Row($kind, $name, $source, $repo, $tracked, $path, $detail) {
    $rows.Add([pscustomobject]@{
        Kind = $kind; Name = $name; Source = $source; Repo = $repo
        Tracked = if ($tracked) { 'yes' } else { 'no' }; Path = $path; Detail = $detail
    })
}

# ---------------------------------------------------------------------------
# 1. Downloaded files and where they came from
# ---------------------------------------------------------------------------
Write-Host 'Scanning downloads for their source...' -ForegroundColor Cyan
$archiveOrigin = @{}   # zip path -> repo info, used to trace mods back to their download
$seen = @{}
foreach ($root in $DownloadRoots | Select-Object -Unique) {
    foreach ($f in Get-Files $root 6 $fileTypes) {
        if ($seen[$f.FullName]) { continue }
        $seen[$f.FullName] = $true
        $o = Get-Origin $f.FullName
        if (-not $o) { continue }
        $info = Get-RepoFromUrl $o.ReferrerUrl
        if (-not $info -or -not $info.Repo) { $h = Get-RepoFromUrl $o.HostUrl; if ($h -and ($h.Repo -or -not $info)) { $info = $h } }
        if (-not $info) {
            # downloaded, but not from a code-hosting site: only report programs in emulation folders
            if ($f.FullName -match '(?i)^C:\\Emulation|Wintainium\\Apps' -and $f.Extension -match '(?i)\.(exe|zip|7z)$' -and $o.HostUrl -match '^https?://') {
                Add-Row 'Download' $f.Name ([uri]$o.HostUrl).Host '' $false $f.FullName 'from a website, not a code host'
            }
            continue
        }
        $archiveOrigin[$f.FullName.ToLower()] = $info
        $detail = if ($info.Repo) { '' } else { 'repo not recorded; see the URL in the Zone.Identifier' }
        Add-Row 'Download' $f.Name $info.Site $info.Repo (Test-TrackedRepo $info.Repo) $f.FullName $detail
    }
}

# ---------------------------------------------------------------------------
# 2. Cloned git repositories
# ---------------------------------------------------------------------------
Write-Host 'Looking for cloned git repositories...' -ForegroundColor Cyan
$repoSeen = @{}
foreach ($root in $RepoRoots | Select-Object -Unique) {
    if (-not $root -or -not (Test-Path -LiteralPath $root)) { continue }
    # walking all of AppData is slow; EmuDeck's folder is searched on its own
    $starts = @($root)
    if ($env:USERPROFILE -and $root -eq $env:USERPROFILE) {
        $starts = @(Get-ChildItem -LiteralPath $root -Directory -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne 'AppData' } | ForEach-Object { $_.FullName })
    }
    $gitDirs = foreach ($st in $starts) {
        Get-ChildItem -LiteralPath $st -Directory -Recurse -Depth 4 -Force -Filter '.git' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '(?i)\\node_modules\\' }
    }
    foreach ($g in $gitDirs) {
        $repoDir = $g.Parent.FullName
        if ($repoSeen[$repoDir]) { continue }
        $repoSeen[$repoDir] = $true
        $url = ''
        $cfg = Join-Path $g.FullName 'config'
        if (Test-Path -LiteralPath $cfg) {
            $m = Select-String -LiteralPath $cfg -Pattern '^\s*url\s*=\s*(.+)$' | Select-Object -First 1
            if ($m) { $url = $m.Matches[0].Groups[1].Value.Trim() }
        }
        $url2 = $url -replace '^git@([^:]+):', 'https://$1/' -replace '\.git$', ''
        $info = Get-RepoFromUrl $url2
        $repo = if ($info) { $info.Repo } else { '' }
        $site = if ($info) { $info.Site } elseif ($url) { $url } else { 'no remote' }
        Add-Row 'Repo' (Split-Path $repoDir -Leaf) $site $repo (Test-TrackedRepo $repo) $repoDir 'cloned with git'
    }
}

# ---------------------------------------------------------------------------
# 3. Programs in emulation folders that Wintainium does not track
# ---------------------------------------------------------------------------
Write-Host 'Checking emulation folders for programs that are not tracked...' -ForegroundColor Cyan
$noise = '(?i)unins|uninstall|crash|report|updater?$|update$|vc_?redist|dxsetup|QtWebEngine|setup|installer|helper|elevate|createdump|\.backup-'
foreach ($root in $AppRoots | Select-Object -Unique) {
    if (-not $root -or -not (Test-Path -LiteralPath $root)) { continue }
    # one row per app folder: the folders directly under the root, or the root itself if it holds exes
    $folders = @(Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch '\.backup-' })
    $folders += Get-Item -LiteralPath $root
    foreach ($dir in $folders) {
        $depth = if ($dir.FullName -eq (Get-Item -LiteralPath $root).FullName) { 0 } else { 2 }
        $exes = @(Get-ChildItem -LiteralPath $dir.FullName -Filter *.exe -File -Recurse -Depth $depth -ErrorAction SilentlyContinue |
            Where-Object { $_.BaseName -notmatch $noise -and $_.FullName -notmatch $skipDirs })
        if ($exes.Count -eq 0) { continue }
        if ($depth -eq 0) {
            foreach ($e in $exes) {
                # loose programs share a folder (like tools), so only an exact match counts as tracked
                $tr = @($tracked | Where-Object { [Environment]::ExpandEnvironmentVariables([string]$_.exe) -ieq $e.FullName }).Count -gt 0
                if ($tr) { continue }
                $o = Get-Origin $e.FullName
                $info = if ($o) { Get-RepoFromUrl $o.HostUrl } else { $null }
                $ver = [string]$e.VersionInfo.ProductVersion
                Add-Row 'App' $e.BaseName $(if ($info) { $info.Site } else { '' }) $(if ($info) { $info.Repo } else { '' }) $false $e.FullName $(if ($ver) { "version $ver" } else { '' })
            }
            continue
        }
        if (Test-TrackedDir $dir.FullName) { continue }
        $main = $exes | Sort-Object Length -Descending | Select-Object -First 1
        $o = Get-Origin $main.FullName
        $info = if ($o) { Get-RepoFromUrl $o.HostUrl } else { $null }
        $ver = [string]$main.VersionInfo.ProductVersion
        Add-Row 'App' $dir.Name $(if ($info) { $info.Site } else { '' }) $(if ($info) { $info.Repo } else { '' }) $false $main.FullName $(if ($ver) { "version $ver" } else { '' })
    }
}

# ---------------------------------------------------------------------------
# 4. Mods, texture packs, cheats and patches inside emulators
# ---------------------------------------------------------------------------
Write-Host 'Listing mods, texture packs, cheats and patches...' -ForegroundColor Cyan
$containers = '(?i)^(load|mods|graphicPacks|GraphicMods|textures|cheats|patches|ResourcePacks|sdmc)$'
$modSeen = @{}
foreach ($root in $ModRoots | Select-Object -Unique) {
    if (-not $root -or -not (Test-Path -LiteralPath $root)) { continue }
    $boxes = Get-ChildItem -LiteralPath $root -Directory -Recurse -Depth 7 -Force -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -match $containers -and
            $_.FullName -notmatch '(?i)[\\/](reshade-shaders|\.backup-[^\\/]+|backend[\\/]configs|Mods & Texture Packs)[\\/]' -and
            $_.FullName -notmatch '(?i)[\\/](roms|saves)[\\/]' -and
            # shipped with the emulator, not added by you: Dolphin's Sys folder, shader packs, bundled resources
            $_.FullName -notmatch '(?i)[\\/](Sys|resources|shaders(_[a-z]+)?)[\\/]' -and
            $_.Parent.Name -notmatch $containers
        }
    foreach ($box in $boxes) {
        # Switch-style layouts keep mods one level down, under a title ID
        $items = @(Get-ChildItem -LiteralPath $box.FullName -Directory -ErrorAction SilentlyContinue)
        $expanded = @()
        foreach ($it in $items) {
            if ($it.Name -match $containers) {
                # Dolphin-style Load\Textures\<game>: the game folders are the mods
                $expanded += @(Get-ChildItem -LiteralPath $it.FullName -Directory -ErrorAction SilentlyContinue)
            } elseif ($it.Name -match '^(?i)[0-9a-f]{16}$|^contents$') {
                $expanded += @(Get-ChildItem -LiteralPath $it.FullName -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                    if ($it.Name -eq 'contents') { Get-ChildItem -LiteralPath $_.FullName -Directory -ErrorAction SilentlyContinue } else { $_ } })
            } else { $expanded += $it }
        }
        $expanded += @(Get-ChildItem -LiteralPath $box.FullName -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -match '(?i)\.(pnach|cht|ips|bps|ups|patch|yml|yaml|zip)$' })
        foreach ($m in $expanded) {
            if ($modSeen[$m.FullName]) { continue }
            if ($box.Name -ieq 'sdmc' -and $m.Name -match '(?i)^(Nintendo 3DS|Nintendo|atmosphere|switch)$') { continue }   # the emulated SD card itself
            $modSeen[$m.FullName] = $true
            if ($m.Name -match '(?i)^downloadedGraphicPacks$') {
                Add-Row 'Mod' 'Cemu community graphic packs' 'Cemu updater' '' $true $m.FullName 'updated by Cemu itself'
                continue
            }
            $files = if ($m.PSIsContainer) { @(Get-ChildItem -LiteralPath $m.FullName -File -Recurse -ErrorAction SilentlyContinue) } else { @($m) }
            if ($files.Count -eq 0) { continue }
            $size = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)
            # where did it come from? look at the download mark on a few of its files
            $src = ''; $repo = ''; $from = ''
            foreach ($f in $files | Select-Object -First 25) {
                $o = Get-Origin $f.FullName
                if (-not $o) { continue }
                $info = Get-RepoFromUrl $o.ReferrerUrl
                if (-not $info) { $info = Get-RepoFromUrl $o.HostUrl }
                if (-not $info -and $o.ReferrerUrl -and $archiveOrigin.ContainsKey($o.ReferrerUrl.ToLower())) { $info = $archiveOrigin[$o.ReferrerUrl.ToLower()] }
                if ($info) { $src = $info.Site; $repo = $info.Repo; break }
                if ($o.ReferrerUrl -match '^[A-Za-z]:\\') { $from = "unpacked from $(Split-Path $o.ReferrerUrl -Leaf)" }
                elseif ($o.HostUrl -match '^https?://') { $src = ([uri]$o.HostUrl).Host; break }
            }
            $game = $m.Parent.Name
            $label = if ($game -match '^(?i)[0-9a-f]{16}$') { "$($m.Name)  [$game]" } else { $m.Name }
            Add-Row 'Mod' $label $src $repo (Test-TrackedRepo $repo) $m.FullName ("$($box.Name), $size MB $from").Trim()
        }
    }
}

# ---------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------
$rows | Export-Csv -LiteralPath $OutCsv -NoTypeInformation -Encoding UTF8
Write-Host ''
foreach ($kind in 'Download', 'Repo', 'App', 'Mod') {
    $set = @($rows | Where-Object Kind -eq $kind)
    $label = @{ Download = 'Downloads from code-hosting sites'; Repo = 'Cloned git repositories'; App = 'Programs not tracked by Wintainium'; Mod = 'Mods, textures, cheats and patches' }[$kind]
    Write-Host "$label ($($set.Count))" -ForegroundColor Cyan
    if ($set.Count -eq 0) { Write-Host '  none found'; Write-Host ''; continue }
    $set | Sort-Object Tracked, Repo, Name | Format-Table @{ n = 'Tracked'; e = { $_.Tracked } }, Name, Source, Repo, Detail -AutoSize -Wrap | Out-String -Width 200 | Write-Host
}
$untracked = @($rows | Where-Object { $_.Repo -and $_.Tracked -eq 'no' } | Select-Object -ExpandProperty Repo -Unique)
if ($untracked.Count) {
    Write-Host "Code-hosting projects found on this PC that Wintainium does not track ($($untracked.Count)):" -ForegroundColor Yellow
    $untracked | Sort-Object | ForEach-Object { Write-Host "  $_" }
} else {
    Write-Host 'Every code-hosting project found is already tracked by Wintainium.' -ForegroundColor Green
}
Write-Host ''
Write-Host "Full list: $OutCsv" -ForegroundColor Cyan
