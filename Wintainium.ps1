<#
.SYNOPSIS
  Wintainium - Obtainium-style updates for the Windows apps you install by hand:
  emulators, PC ports, mods and tools published on GitHub, GitLab, Gitea/Forgejo,
  itch.io and a few project-specific feeds.

  It shows what is outdated and, for the app you pick, starts the right kind of update:
    emudeck    runs EmuDeck's own install function for that emulator
    inapp      starts the app so you can use its own updater
    installer  downloads the new installer (checksum-verified) and offers to run it
    zip        downloads the new archive, backs up the app folder, unpacks over it
    download   downloads the new file and shows it in Explorer
    page       opens the release page
  Mods and data packs (no program inside) are tracked by the release tag that is installed.
  Apps and their update methods live in wintainium-apps.json next to this script
  (see wintainium-apps.example.json).

.EXAMPLE
  .\Wintainium.ps1           # console menu
  .\Wintainium.ps1 gui       # window
  .\Wintainium.ps1 list      # just print the table
  .\Wintainium.ps1 token     # save or replace a GitHub token (encrypted with DPAPI)
  .\Wintainium.ps1 shortcut  # desktop shortcut that opens the window

.NOTES
  https://github.com/  (Wintainium) - GPL-3.0. Not affiliated with Obtainium, EmuDeck or Microsoft.
  GitHub allows 60 anonymous API calls per hour; one refresh uses about one per app.
  Run '.\Wintainium.ps1 token' once to save a scopeless GitHub token and raise that limit.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][ValidateSet('menu', 'list', 'token', 'gui', 'shortcut')][string]$Command = 'menu',
    # Defaults to wintainium-apps.json next to this script.
    [string]$Config = '',
    [string]$EmuDeckExe = "$env:LOCALAPPDATA\Programs\EmuDeck\EmuDeck.exe",
    [string]$DownloadDir = "$env:USERPROFILE\Downloads\Wintainium",
    # Where 'add app' installs something new by default.
    [string]$InstallRoot = "$env:USERPROFILE\Wintainium\Apps",
    # A release counts as newer only if it is this many days newer than your .exe.
    [int]$GraceDays = 3,
    # Release lookups are remembered this long (wintainium-cache.json) so refreshes do not use up GitHub's hourly limit.
    [int]$CacheMinutes = 60
)

$script:AppVersion = '0.1.1'

# The script's own folder. Windows PowerShell 5.1 leaves $PSScriptRoot empty while the
# param() defaults are evaluated when started with -File (as the shortcut does), so resolve it here.
$script:Here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
if (-not $Config) { $Config = Join-Path $script:Here 'wintainium-apps.json' }

[Net.ServicePointManager]::SecurityProtocol =
    [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$ProgressPreference = 'SilentlyContinue'

$ua = @{ 'User-Agent' = "Wintainium/$script:AppVersion" }
$ghHeaders = @{ 'User-Agent' = 'Wintainium'; 'Accept' = 'application/vnd.github+json' }

# Earlier versions used emu-*.json / emu-token.xml; copy them over once if found.
foreach ($pair in @(@('emu-apps.json', 'wintainium-apps.json'), @('emu-cache.json', 'wintainium-cache.json'), @('emu-token.xml', 'wintainium-token.xml'))) {
    $old = Join-Path $script:Here $pair[0]; $new = Join-Path $script:Here $pair[1]
    if ((Test-Path -LiteralPath $old) -and -not (Test-Path -LiteralPath $new)) { Copy-Item -LiteralPath $old -Destination $new }
}

# ---- GitHub token ---------------------------------------------------------
# Stored with Export-Clixml, which encrypts it with Windows DPAPI: only this Windows
# user on this PC can read it back. Set or replace it with:  .\Wintainium.ps1 token
$script:tokenFile = Join-Path $script:Here 'wintainium-token.xml'
$script:tokenExpires = $null

function Get-StoredToken {
    if ($env:GITHUB_TOKEN) { return $env:GITHUB_TOKEN }
    if (-not (Test-Path -LiteralPath $script:tokenFile)) { return $null }
    try {
        $t = Import-Clixml -LiteralPath $script:tokenFile
        $script:tokenExpires = $t.Expires
        return (New-Object Net.NetworkCredential('', $t.Token)).Password
    } catch {
        Write-Warning "Could not read $($script:tokenFile). Run '.\Wintainium.ps1 token' to save it again."
        return $null
    }
}

function Set-StoredToken {
    # Pasting into a hidden prompt is unreliable in the Windows console, so take the token
    # from the GITHUB_TOKEN variable if it is set, otherwise straight from the clipboard.
    $plain = ''
    if ($env:GITHUB_TOKEN -and (Read-Host 'Use the token already in GITHUB_TOKEN? (Y/n)') -notmatch '^n') {
        $plain = $env:GITHUB_TOKEN
    } else {
        Read-Host 'Copy the token to the clipboard, then press Enter here' | Out-Null
        $plain = [string](Get-Clipboard | Select-Object -First 1)
        Set-Clipboard -Value ' '
    }
    $plain = $plain.Trim()
    if (-not $plain) { Write-Host 'No token found. Cancelled.'; return }
    $sec = ConvertTo-SecureString $plain -AsPlainText -Force
    try {
        $rate = (Invoke-RestMethod 'https://api.github.com/rate_limit' -Headers @{ 'User-Agent' = 'Wintainium'; 'Authorization' = "Bearer $plain" }).rate
    } catch {
        Write-Host 'GitHub rejected that token, so nothing was saved.' -ForegroundColor Red
        return
    }
    $days = Read-Host 'Days until the token expires (Enter = 60)'
    if ($days -notmatch '^\d+$') { $days = 60 }
    [pscustomobject]@{ Token = $sec; Expires = (Get-Date).AddDays([int]$days) } | Export-Clixml -LiteralPath $script:tokenFile
    Write-Host "Saved to $($script:tokenFile). GitHub limit is now $($rate.limit) lookups per hour." -ForegroundColor Green
}

$storedToken = Get-StoredToken
if ($storedToken) { $ghHeaders['Authorization'] = "Bearer $storedToken" }

$script:sevenZip = "$env:APPDATA\EmuDeck\backend\wintools\7z.exe"
$viaLabel = @{ emudeck = 'EmuDeck'; inapp = 'in-app'; installer = 'installer'; zip = 'zip install'; download = 'download'; page = 'release page' }

function ConvertTo-Utc($value) {
    if (-not $value) { return $null }
    return ([datetime]$value).ToUniversalTime()
}

function Cut([string]$s, [int]$n) {
    if ($s.Length -gt $n) { return $s.Substring(0, $n - 3) + '...' }
    return $s
}

# Picks the Windows upload (or the one matching $assetPattern) from an itch.io game page.
function Read-ItchPage([string]$html, [string]$page, [string]$assetPattern) {
    $starts = @([regex]::Matches($html, 'class="name">([^<]+)</strong>'))
    if ($starts.Count -eq 0) { throw 'no downloads listed on the itch.io page' }
    $uploads = for ($i = 0; $i -lt $starts.Count; $i++) {
        $from = $starts[$i].Index
        $to = if ($i + 1 -lt $starts.Count) { $starts[$i + 1].Index } else { [Math]::Min($html.Length, $from + 4000) }
        $chunk = $html.Substring($from, $to - $from)
        $ver = if ($chunk -match 'class="version_name">\s*Version\s+([^<]+?)\s*</span>') { $Matches[1] } else { '' }
        $date = $null
        if ($chunk -match '<abbr title="(\d{1,2} \w+ \d{4} @ \d{1,2}:\d{2}) UTC"') {
            $date = [datetime]::ParseExact($Matches[1], 'd MMMM yyyy @ H:mm', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]'AssumeUniversal,AdjustToUniversal')
        }
        [pscustomobject]@{ Name = $starts[$i].Groups[1].Value.Trim(); Version = $ver; Date = $date; Windows = ($chunk -match 'icon-windows') }
    }
    $pick = $null
    if ($assetPattern) { $pick = $uploads | Where-Object { $_.Name -match $assetPattern } | Select-Object -First 1 }
    if (-not $pick) { $pick = $uploads | Where-Object Windows | Select-Object -First 1 }
    if (-not $pick) { $pick = $uploads | Select-Object -First 1 }
    $tag = if ($pick.Version) { $pick.Version } else { $pick.Name }
    return [pscustomobject]@{ Tag = $tag; Date = $pick.Date; Page = $page; Assets = @(); Notes = "Latest upload: $($pick.Name)" }
}

function Get-LatestRelease($app) {
    if ($app.source -eq 'github') {
        $base = "https://api.github.com/repos/$($app.repo)"
        if ($app.tag) {
            $r = Invoke-RestMethod "$base/releases/tags/$($app.tag)" -Headers $ghHeaders
            $date = $r.assets | ForEach-Object { ConvertTo-Utc $_.updated_at } | Sort-Object | Select-Object -Last 1
            if (-not $date) { $date = ConvertTo-Utc $r.published_at }
        } elseif ($app.prerelease) {
            $list = Invoke-RestMethod "$base/releases?per_page=10" -Headers $ghHeaders
            $r = $list | Where-Object { -not $_.draft } | Select-Object -First 1
            $date = ConvertTo-Utc $r.published_at
        } else {
            $r = Invoke-RestMethod "$base/releases/latest" -Headers $ghHeaders
            $date = ConvertTo-Utc $r.published_at
        }
    } elseif ($app.source -eq 'gitea') {
        $r = Invoke-RestMethod $app.api -Headers $ua
        $date = ConvertTo-Utc $r.published_at
    } elseif ($app.source -eq 'gitlab') {
        $g = Invoke-RestMethod $app.api -Headers $ua
        $assets = @($g.assets.links | ForEach-Object { [pscustomobject]@{ name = $_.name; browser_download_url = $_.url } })
        return [pscustomobject]@{ Tag = $g.tag_name; Date = (ConvertTo-Utc $g.released_at); Page = $g._links.self; Assets = $assets; Notes = [string]$g.description }
    } elseif ($app.source -eq 'itch') {
        # itch.io has no public release API, so read the game page's download list
        $html = (Invoke-WebRequest -Uri ([string]$app.url) -UseBasicParsing -Headers $ua).Content
        return (Read-ItchPage $html ([string]$app.url) ([string]$app.asset))
    } elseif ($app.source -eq 'dolphin') {
        $d = Invoke-RestMethod $app.api -Headers $ua
        $assets = @($d.artifacts | Where-Object { $_.system -eq 'Windows x64' } | ForEach-Object {
            [pscustomobject]@{ name = [IO.Path]::GetFileName($_.url); browser_download_url = $_.url } })
        $notes = ([string]$d.changelog_html -replace '<[^>]+>', '' -replace '&amp;', '&' -replace '&quot;', '"' -replace '&#39;', "'").Trim()
        return [pscustomobject]@{ Tag = $d.shortrev; Date = (ConvertTo-Utc $d.date); Page = 'https://dolphin-emu.org/download/'; Assets = $assets; Notes = $notes }
    } else {
        return $null
    }
    if (-not $r) { throw 'no release found' }
    [pscustomobject]@{ Tag = $r.tag_name; Date = $date; Page = $r.html_url; Assets = @($r.assets); Notes = [string]$r.body }
}

# ---- release cache --------------------------------------------------------
$script:cacheFile = Join-Path $script:Here 'wintainium-cache.json'
$script:cache = @{}
if (Test-Path -LiteralPath $script:cacheFile) {
    try {
        $saved = Get-Content -LiteralPath $script:cacheFile -Raw | ConvertFrom-Json
        foreach ($p in $saved.PSObject.Properties) { $script:cache[$p.Name] = $p.Value }
    } catch { $script:cache = @{} }
}

function Get-LatestCached($app, [switch]$Force) {
    $key = [string]$app.name
    $c = $script:cache[$key]
    if (-not $Force -and $c -and $c.Fetched) {
        $age = ((Get-Date).ToUniversalTime() - (ConvertTo-Utc $c.Fetched)).TotalMinutes
        if ($age -lt $CacheMinutes) {
            return [pscustomobject]@{ Tag = $c.Tag; Date = (ConvertTo-Utc $c.Date); Page = $c.Page; Assets = @($c.Assets); Notes = [string]$c.Notes }
        }
    }
    $l = Get-LatestRelease $app
    if ($l) {
        $script:cache[$key] = [pscustomobject]@{
            Tag     = $l.Tag
            Date    = if ($l.Date) { $l.Date.ToString('o') } else { $null }
            Page    = $l.Page
            Assets  = @($l.Assets | ForEach-Object { [pscustomobject]@{ name = $_.name; browser_download_url = $_.browser_download_url } })
            Notes   = if ($l.Notes -and $l.Notes.Length -gt 20000) { $l.Notes.Substring(0, 20000) } else { [string]$l.Notes }
            Fetched = (Get-Date).ToUniversalTime().ToString('o')
        }
    }
    return $l
}

function Save-Cache {
    try { $script:cache | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $script:cacheFile -Encoding UTF8 } catch { }
}

function Get-AppRows([switch]$Force) {
    $rows = foreach ($app in $script:apps) {
        Write-Host "`r  checking $($app.name)...".PadRight(60) -NoNewline
        $exe = [Environment]::ExpandEnvironmentVariables([string]$app.exe)
        $installedVer = ''
        $exeUtc = $null
        $found = Test-Path -LiteralPath $exe
        if ($found) {
            $item = Get-Item -LiteralPath $exe
            $exeUtc = $item.LastWriteTimeUtc
            $installedVer = [string]$item.VersionInfo.ProductVersion
        }

        $latest = $null
        $status = ''
        $page = [string]$app.url
        if ($app.source -eq 'manual') {
            $status = 'check manually'
        } else {
            try {
                $latest = Get-LatestCached $app -Force:$Force
                $page = $latest.Page
            } catch {
                $status = 'lookup failed'
                $why = "$($_.Exception.Message) $($_.ErrorDetails.Message)"
                if ($why -match '401|Bad credentials') { $status = 'token rejected' }
                elseif ($why -match '403|429|rate limit') { $status = 'rate limited' }
            }
        }

        if (-not $found) {
            $status = 'exe not found'
        } elseif ($latest -and $app.installedTag) {
            # mods and data packs have no program to read a version from, so compare the
            # release that was installed (recorded by add app and by each zip update)
            $status = if ([string]$latest.Tag -eq [string]$app.installedTag) { 'up to date' } else { 'UPDATE' }
        } elseif ($latest) {
            $grace = if ($app.graceDays) { [int]$app.graceDays } else { $GraceDays }
            $num = ([string]$latest.Tag) -replace '^[^\d]*', ''
            if ($installedVer -and $num -and $installedVer.Contains($num)) {
                $status = 'up to date'
            } elseif ($latest.Date -and $latest.Date -gt $exeUtc.AddDays($grace)) {
                $status = 'UPDATE'
            } else {
                $status = 'up to date'
            }
        }

        $methods = @($app.update | Where-Object { $_ })
        if ($methods.Count -eq 0) { $methods = @('page') }
        [pscustomobject]@{
            App       = [string]$app.name
            Status    = $status
            Installed = if ($installedVer) { $installedVer } elseif ($app.installedTag) { [string]$app.installedTag } elseif ($exeUtc) { $exeUtc.ToLocalTime().ToString('yyyy-MM-dd') } else { '' }
            Latest    = if ($latest) { [string]$latest.Tag } else { '' }
            Released  = if ($latest -and $latest.Date) { $latest.Date.ToLocalTime().ToString('yyyy-MM-dd') } else { '' }
            Methods   = $methods
            Via       = $viaLabel[[string]$methods[0]]
            Page      = $page
            Exe       = $exe
            Assets    = if ($latest) { $latest.Assets } else { @() }
            Notes     = if ($latest) { [string]$latest.Notes } else { '' }
            Def       = $app
        }
    }
    Write-Host ("`r" + (' ' * 60) + "`r") -NoNewline
    Save-Cache
    $order = @{ 'UPDATE' = 0; 'token rejected' = 1; 'rate limited' = 1; 'lookup failed' = 1; 'exe not found' = 2; 'check manually' = 3; 'up to date' = 4 }
    return @($rows | Sort-Object { $order[$_.Status] }, App)
}

function Show-Table($rows) {
    $fmt = ' {0,2}  {1,-25} {2,-15} {3,-17} {4,-25} {5,-11} {6}'
    Write-Host ''
    Write-Host ($fmt -f '#', 'App', 'Status', 'Installed', 'Latest', 'Released', 'Update via') -ForegroundColor Cyan
    for ($i = 0; $i -lt $rows.Count; $i++) {
        $r = $rows[$i]
        $color = 'Gray'
        if ($r.Status -eq 'UPDATE') { $color = 'Yellow' }
        elseif ($r.Status -eq 'up to date') { $color = 'DarkGray' }
        elseif ($r.Status -ne 'check manually') { $color = 'Red' }
        $line = $fmt -f ($i + 1), (Cut $r.App 25), $r.Status, (Cut $r.Installed 17), (Cut $r.Latest 25), $r.Released, $viaLabel[[string]$r.Methods[0]]
        Write-Host $line -ForegroundColor $color
    }
    $n = @($rows | Where-Object { $_.Status -eq 'UPDATE' }).Count
    Write-Host ''
    Write-Host "$n update(s) available." -ForegroundColor Cyan
    if (@($rows | Where-Object { $_.Status -eq 'rate limited' }).Count -gt 0) {
        Write-Host 'Some lookups hit GitHub''s hourly limit (60 without a token). They will work again within the hour,' -ForegroundColor Yellow
        Write-Host 'or save a GitHub token with:  .\Wintainium.ps1 token' -ForegroundColor Yellow
    }
    if (@($rows | Where-Object { $_.Status -eq 'token rejected' }).Count -gt 0) {
        Write-Host 'GitHub rejected the saved token (expired or revoked). Make a new one and run:  .\Wintainium.ps1 token' -ForegroundColor Yellow
    } elseif ($script:tokenExpires) {
        $left = [int]([datetime]$script:tokenExpires - (Get-Date)).TotalDays
        if ($left -le 7) { Write-Host "The saved GitHub token expires in about $left day(s). Make a new one and run:  .\Wintainium.ps1 token" -ForegroundColor Yellow }
    }
}

function Open-EmuDeck([string[]]$names) {
    Write-Host ''
    Write-Host "EmuDeck manages: $($names -join ', ')" -ForegroundColor Cyan
    Write-Host '  In EmuDeck: Manage Emulators > click the emulator > ReInstall / Update. Repeat for each one listed.'
    Write-Host '  Leave "Reset configuration" and "Uninstall" alone - those change settings or remove the emulator.'
    if (Test-Path -LiteralPath $EmuDeckExe) {
        Start-Process -FilePath $EmuDeckExe
    } else {
        Write-Host "  EmuDeck not found at $EmuDeckExe - open it yourself." -ForegroundColor Red
    }
}

function Invoke-EmuDeckInstall($row, [switch]$Yes) {
    $fn = [string]$row.Def.emudeckFn
    $all = "$env:APPDATA\EmuDeck\backend\functions\all.ps1"
    if (-not $fn -or -not (Test-Path -LiteralPath $all)) { Open-EmuDeck @($row.App); return }

    $procName = [IO.Path]::GetFileNameWithoutExtension($row.Exe)
    if (Get-Process -Name $procName -ErrorAction SilentlyContinue) {
        Write-Host "  $($row.App) is running. Close it and try again." -ForegroundColor Red
        return
    }
    $dir = Split-Path -Path $row.Exe -Parent
    if (-not $Yes) {
        Write-Host "  This runs EmuDeck's own installer function ($fn)."
        Write-Host "  It downloads the build EmuDeck is set to use and copies it over $dir"
        Write-Host '  It does not run the configuration step, so settings, saves and mods stay as they are.'
        if ((Read-Host '  Continue? (y/N)') -notmatch '^y') { Write-Host '  Cancelled.'; return }
    }
    Write-Host "  Updating $($row.App) through EmuDeck..." -ForegroundColor Cyan
    $before = if (Test-Path -LiteralPath $row.Exe) { (Get-Item -LiteralPath $row.Exe).LastWriteTime } else { $null }
    # Run EmuDeck's function in a background job so progress can be shown while it works.
    $tempDir = "$env:APPDATA\EmuDeck\temp"
    $job = Start-Job -ScriptBlock {
        param($all, $fn)
        Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
        . $all
        & $fn
    } -ArgumentList $all, $fn
    $started = Get-Date
    while ($job.State -eq 'Running') {
        $mb = 0
        if (Test-Path -LiteralPath $tempDir) {
            $sum = (Get-ChildItem -LiteralPath $tempDir -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
            if ($sum) { $mb = [math]::Round($sum / 1MB) }
        }
        $secs = [int]((Get-Date) - $started).TotalSeconds
        Write-Host ("`r  working... {0}s elapsed, {1} MB in EmuDeck's temp folder   " -f $secs, $mb) -NoNewline
        Start-Sleep -Seconds 2
    }
    Write-Host ''
    $jobErrors = @()
    Receive-Job -Job $job -ErrorVariable jobErrors -ErrorAction SilentlyContinue | Out-Null
    Remove-Job -Job $job -Force
    if ($jobErrors.Count -gt 0) { Write-Host "  EmuDeck reported: $($jobErrors[0])" -ForegroundColor Yellow }
    $after = if (Test-Path -LiteralPath $row.Exe) { (Get-Item -LiteralPath $row.Exe).LastWriteTime } else { $null }
    if ($after -and $after -ne $before) {
        Write-Host "  Done. $([IO.Path]::GetFileName($row.Exe)) is now dated $($after.ToString('yyyy-MM-dd'))." -ForegroundColor Green
    } else {
        Write-Host '  Finished, but the exe did not change. EmuDeck may be pinned to this build, or the download failed.' -ForegroundColor Yellow
        Write-Host "  Log: $env:APPDATA\EmuDeck\logs"
    }
}

function Select-Asset($row) {
    # Some projects host their downloads off GitHub; "downloadUrl" in wintainium-apps.json builds the
    # link from the release version, e.g. https://example.org/{version}/app-{version}.zip
    if ($row.Def.downloadUrl -and $row.Latest) {
        $ver = ([string]$row.Latest) -replace '^[^\d]*', ''
        $url = ([string]$row.Def.downloadUrl).Replace('{version}', $ver)
        return [pscustomobject]@{ name = [IO.Path]::GetFileName(([uri]$url).AbsolutePath); browser_download_url = $url }
    }
    $assets = @($row.Assets | Where-Object { $_.browser_download_url })
    if ($row.Def.asset) { $assets = @($assets | Where-Object { $_.name -match $row.Def.asset }) }
    if ($assets.Count -eq 0) { return $null }
    if ($assets.Count -eq 1) { return $assets[0] }
    Write-Host '  Which file?'
    for ($i = 0; $i -lt $assets.Count; $i++) { Write-Host ("   {0,2}  {1}" -f ($i + 1), $assets[$i].name) }
    $pick = Read-Host '  number (Enter to cancel)'
    if ($pick -match '^\d+$' -and [int]$pick -ge 1 -and [int]$pick -le $assets.Count) { return $assets[[int]$pick - 1] }
    return $null
}

# Downloads with a live size/time line (keeps the window responsive), tags the file with
# Windows' "downloaded from the internet" mark so SmartScreen still checks it, and notices
# when Windows Security removes the file the moment it lands.
function Save-Download([string]$url, [string]$file, [string]$label) {
    Write-Host "  Downloading $label..."
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    $wc = New-Object Net.WebClient
    $wc.Headers['User-Agent'] = 'Wintainium'
    $task = $wc.DownloadFileTaskAsync([uri]$url, $file)
    $start = Get-Date
    while (-not $task.IsCompleted) {
        Start-Sleep -Milliseconds 300
        $mb = 0
        if (Test-Path -LiteralPath $file) { $mb = [math]::Round((Get-Item -LiteralPath $file).Length / 1MB, 1) }
        Write-Host ("`r  downloading {0}... {1} MB, {2}s   " -f $label, $mb, [int]((Get-Date) - $start).TotalSeconds) -NoNewline
    }
    Write-Host ''
    $wc.Dispose()
    if ($task.IsFaulted -or $task.IsCanceled) {
        $why = if ($task.Exception) { $task.Exception.GetBaseException().Message } else { 'cancelled' }
        Write-Host "  Download failed: $why" -ForegroundColor Red
        Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
        return 'failed'
    }
    try { Set-Content -LiteralPath $file -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3`r`nHostUrl=$url" -ErrorAction Stop } catch { Write-Verbose 'Could not add the download mark.' }
    Start-Sleep -Milliseconds 1500
    if (-not (Test-Path -LiteralPath $file)) {
        return 'removed'
    }
    return 'ok'
}

# Finds the SHA-256 the release publishes for a file (file.sha256, SHA256SUMS, checksums.txt).
# Returns @{ Hash; From } or $null, and @{ From; Error } when the checksum file exists but cannot be used.
function Get-PublishedHash($row, [string]$name) {
    $all = @($row.Assets | Where-Object { $_.browser_download_url })
    $sum = $all | Where-Object { $_.name -ieq "$name.sha256" } | Select-Object -First 1
    $perFile = [bool]$sum
    if (-not $sum) {
        $sum = $all | Where-Object { $_.name -match '(?i)^(sha256sums|checksums?|sha256|hashes)(\.txt|\.sha256)?$' } | Select-Object -First 1
    }
    if (-not $sum) { return $null }
    try {
        $wc = New-Object Net.WebClient
        $wc.Headers['User-Agent'] = 'Wintainium'
        $text = $wc.DownloadString([string]$sum.browser_download_url)
        $wc.Dispose()
    } catch {
        return @{ From = $sum.name; Error = "Could not read $($sum.name)" }
    }
    if ($perFile) {
        if ($text -match '(?i)\b([0-9a-f]{64})\b') { return @{ Hash = $Matches[1].ToLower(); From = $sum.name } }
    } else {
        $line = ($text -split "`n") | Where-Object { $_ -match [regex]::Escape($name) } | Select-Object -First 1
        if ($line -and $line -match '(?i)\b([0-9a-f]{64})\b') { return @{ Hash = $Matches[1].ToLower(); From = $sum.name } }
    }
    return @{ From = $sum.name; Error = "$($sum.name) does not list this file" }
}

# Compares a download with its published SHA-256.
# Returns 'ok', 'bad' (mismatch; the caller deletes it) or 'removed' (Windows Security took the file meanwhile).
function Test-Checksum($row, $asset, [string]$file) {
    $p = Get-PublishedHash $row ([string]$asset.name)
    if (-not $p) { Write-Host '  No checksum is published for this file, so it was not verified.' -ForegroundColor DarkGray; return 'ok' }
    if ($p.Error) { Write-Host "  $($p.Error), so the download was not verified." -ForegroundColor Yellow; return 'ok' }
    $actual = $null
    try { $actual = [string](Get-FileHash -LiteralPath $file -Algorithm SHA256 -ErrorAction Stop).Hash } catch { $actual = $null }
    if (-not $actual) {
        # the file vanished or was locked: Windows Security quarantines files a few seconds after they land
        Start-Sleep -Seconds 2
        if (-not (Test-Path -LiteralPath $file)) { return 'removed' }
        Write-Host "  Could not read $([IO.Path]::GetFileName($file)) to check it (another program has it locked). Not using it." -ForegroundColor Red
        return 'bad'
    }
    $actual = $actual.ToLower()
    if ($actual -eq $p.Hash) {
        Write-Host "  SHA-256 matches the published checksum ($($p.From))." -ForegroundColor Green
        return 'ok'
    }
    Write-Host "  SHA-256 does NOT match the published checksum ($($p.From)). The download was deleted." -ForegroundColor Red
    Write-Host "    expected $($p.Hash)" -ForegroundColor Red
    Write-Host "    got      $actual" -ForegroundColor Red
    return 'bad'
}

# Windows Security removed a file. Explain, and offer the safe ways to check it.
# This never restores files, allows threats or adds exclusions: that stays the user's decision.
function Show-QuarantineHelp($row, [string]$name) {
    Write-Host "  Windows Security removed $name." -ForegroundColor Red
    try {
        $det = Get-MpThreatDetection -ErrorAction Stop | Where-Object { "$($_.Resources)" -match [regex]::Escape($name) } |
            Sort-Object InitialDetectionTime -Descending | Select-Object -First 1
        if ($det) {
            $t = Get-MpThreat -ThreatID $det.ThreatID -ErrorAction Stop | Select-Object -First 1
            Write-Host "  Detection: $($t.ThreatName) (threat ID $($det.ThreatID))" -ForegroundColor Red
            if ($t.ThreatName -match '!ml$|Wacatac') {
                Write-Host '  That is a generic machine-learning detection, which small unsigned tools often trigger.' -ForegroundColor Yellow
                Write-Host '  It is not proof either way.' -ForegroundColor Yellow
            }
        }
    } catch { Write-Host '  (Run as administrator to see the detection name here.)' -ForegroundColor DarkGray }
    Write-Host '  Do not use "Allow on device": it allows the threat ID, which for a generic detection' -ForegroundColor Yellow
    Write-Host '  also covers unrelated files. Avoid folder exclusions for the same reason.' -ForegroundColor Yellow
    $p = Get-PublishedHash $row $name
    if ($p -and $p.Hash) {
        Write-Host "  Published SHA-256 ($($p.From)): $($p.Hash)"
        if ((Read-Host '  Look this hash up on VirusTotal (no upload needed)? (y/N)') -match '^y') {
            Start-Process "https://www.virustotal.com/gui/file/$($p.Hash)"
        }
    }
    if ((Read-Host '  Open Microsoft''s form to report a false positive? (y/N)') -match '^y') {
        Start-Process 'https://www.microsoft.com/en-us/wdsi/filesubmission'
    }
}

function Get-AssetFile($row) {
    $asset = Select-Asset $row
    if (-not $asset) {
        Write-Host '  No matching download on that release. Opening the release page instead.' -ForegroundColor Yellow
        if ($row.Page) { Start-Process $row.Page }
        return $null
    }
    if (-not (Test-Path -LiteralPath $DownloadDir)) { New-Item -ItemType Directory -Path $DownloadDir | Out-Null }
    $file = Join-Path $DownloadDir $asset.name
    $got = Save-Download ([string]$asset.browser_download_url) $file ([string]$asset.name)
    if ($got -eq 'removed') { Show-QuarantineHelp $row ([string]$asset.name); return $null }
    if ($got -ne 'ok') { return $null }
    $check = Test-Checksum $row $asset $file
    if ($check -eq 'removed') { Show-QuarantineHelp $row ([string]$asset.name); return $null }
    if ($check -ne 'ok') {
        Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
        return $null
    }
    return $file
}

function Test-Archive([string]$file) {
    $ext = [IO.Path]::GetExtension($file).ToLower()
    if ($ext -eq '.zip') { return $true }
    if ($ext -eq '.7z' -and (Test-Path -LiteralPath $script:sevenZip)) { return $true }
    return $false
}

# Unpacks an archive into $dir, creating it if needed. Existing files with the same name are replaced.
function Expand-ToFolder([string]$file, [string]$dir) {
    $tmp = Join-Path $env:TEMP ("wintainium-" + [guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $script:sevenZip) { & $script:sevenZip x "-o$tmp" -aoa $file | Out-Null }
    else { Expand-Archive -LiteralPath $file -DestinationPath $tmp }
    $src = $tmp
    $kids = @(Get-ChildItem -LiteralPath $tmp -Force)
    if ($kids.Count -eq 1 -and $kids[0].PSIsContainer) { $src = $kids[0].FullName }
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Get-ChildItem -LiteralPath $src -Force | Copy-Item -Destination $dir -Recurse -Force
    Remove-Item -LiteralPath $tmp -Recurse -Force
}

# The folder an app lives in. Mods and data packs set "installDir", because the file
# they are tracked by can sit a few folders deep (e.g. <mod>\patches\<file>.ips).
function Get-AppFolder($row) {
    if ($row.Def -and $row.Def.installDir) { return [Environment]::ExpandEnvironmentVariables([string]$row.Def.installDir) }
    return (Split-Path -Path $row.Exe -Parent)
}

# Records the release now installed, for apps tracked by release tag (mods, data packs).
function Save-InstalledTag($row) {
    $app = $script:apps | Where-Object { [string]$_.name -eq [string]$row.App } | Select-Object -First 1
    if (-not $app -or -not $row.Latest) { return }
    $app | Add-Member -NotePropertyName installedTag -NotePropertyValue ([string]$row.Latest) -Force
    ConvertTo-Json -InputObject @($script:apps) -Depth 6 | Set-Content -LiteralPath $Config -Encoding UTF8
}

# Returns $true when the new files were unpacked.
function Install-Zip($row, [string]$file) {
    $dir = Get-AppFolder $row
    if (-not (Test-Archive $file)) {
        Write-Host "  I cannot unpack this file type here. It is saved at: $file" -ForegroundColor Yellow
        Start-Process explorer.exe "/select,`"$file`""
        return $false
    }
    if ([IO.Path]::GetExtension($row.Exe) -ieq '.exe') {
        $procName = [IO.Path]::GetFileNameWithoutExtension($row.Exe)
        if (Get-Process -Name $procName -ErrorAction SilentlyContinue) {
            Write-Host "  $($row.App) is running. Close it and try again." -ForegroundColor Red
            return $false
        }
    }
    $backup = "$dir.backup-$(Get-Date -Format 'yyyyMMdd-HHmm')"
    Write-Host "  This will back up $dir"
    Write-Host "                 to $backup"
    Write-Host '  and then unpack the new files over the original folder.'
    if ((Read-Host '  Continue? (y/N)') -notmatch '^y') { Write-Host '  Cancelled.'; return $false }

    Copy-Item -LiteralPath $dir -Destination $backup -Recurse
    Expand-ToFolder $file $dir
    Write-Host "  Done. If anything is wrong, delete $dir and rename the backup back." -ForegroundColor Green
    if ($row.Def.hint) { Write-Host "  Then: $($row.Def.hint)" }
    return $true
}

function Invoke-Method($row, [string]$method, [switch]$Yes) {
    switch ($method) {
        'emudeck' { Invoke-EmuDeckInstall $row -Yes:$Yes }
        'inapp' {
            Write-Host "  Starting $($row.App)." -ForegroundColor Cyan
            if ($row.Def.hint) { Write-Host "  Then: $($row.Def.hint)" }
            # "launch" in wintainium-apps.json overrides the exe when the updater lives in a launcher or script
            $start = $row.Exe
            if ($row.Def.launch) { $start = [Environment]::ExpandEnvironmentVariables([string]$row.Def.launch) }
            if (Test-Path -LiteralPath $start) {
                Start-Process -FilePath $start -WorkingDirectory (Split-Path -Path $start -Parent)
            } else {
                Write-Host "  Not found: $start" -ForegroundColor Red
            }
        }
        'installer' {
            $file = Get-AssetFile $row
            if ($file) {
                Write-Host "  Saved to $file"
                if ((Read-Host '  Run the installer now? (y/N)') -match '^y') { Start-Process -FilePath $file }
            }
        }
        'zip' {
            $file = Get-AssetFile $row
            if ($file -and (Install-Zip $row $file) -and $row.Def.installedTag) { Save-InstalledTag $row }
        }
        'download' {
            $file = Get-AssetFile $row
            if ($file) {
                Write-Host "  Saved to $file"
                Start-Process explorer.exe "/select,`"$file`""
            }
        }
        default {
            if ($row.Page) { Start-Process $row.Page }
            else { Write-Host '  No release page is set for this app. Add its repo or url to wintainium-apps.json.' -ForegroundColor Yellow }
        }
    }
}

function Update-App($row) {
    Write-Host ''
    Write-Host "$($row.App): installed $($row.Installed), latest $($row.Latest) ($($row.Released))" -ForegroundColor Cyan
    $methods = @($row.Methods)
    $method = [string]$methods[0]
    if ($methods.Count -gt 1) {
        for ($i = 0; $i -lt $methods.Count; $i++) { Write-Host ("   {0}  {1}" -f ($i + 1), $viaLabel[[string]$methods[$i]]) }
        $pick = Read-Host "  how? (Enter = 1, x = cancel)"
        if ($pick -match '^x') { return }
        if ($pick -match '^\d+$' -and [int]$pick -ge 1 -and [int]$pick -le $methods.Count) { $method = [string]$methods[[int]$pick - 1] }
    }
    Invoke-Method $row $method
}

function Start-App($row) {
    $start = $row.Exe
    if ($row.Def.launch) { $start = [Environment]::ExpandEnvironmentVariables([string]$row.Def.launch) }
    elseif ([IO.Path]::GetExtension($start) -notmatch '(?i)^\.(exe|bat|cmd|lnk)$') {
        # a mod or data pack has nothing to run, so show its folder instead
        $dir = Get-AppFolder $row
        if (Test-Path -LiteralPath $dir) { Write-Host "  $($row.App) has no program to start. Opening its folder."; Start-Process explorer.exe "`"$dir`"" }
        else { Write-Host "  Folder not found: $dir" -ForegroundColor Red }
        return
    }
    if (-not (Test-Path -LiteralPath $start)) { Write-Host "  Not found: $start" -ForegroundColor Red; return }
    Write-Host "  Starting $($row.App)."
    Start-Process -FilePath $start -WorkingDirectory (Split-Path -Path $start -Parent)
}

function Remove-AppEntry($row) {
    $ans = Read-Host "Remove $($row.App) from the list? Its files stay where they are. (y/N)"
    if ($ans -notmatch '^y') { return $false }
    $script:apps = @($script:apps | Where-Object { [string]$_.name -ne [string]$row.App })
    ConvertTo-Json -InputObject @($script:apps) -Depth 6 | Set-Content -LiteralPath $Config -Encoding UTF8
    if ($script:cache.ContainsKey([string]$row.App)) { $script:cache.Remove([string]$row.App); Save-Cache }
    Write-Host "  Removed $($row.App) from the list." -ForegroundColor Green
    return $true
}

function Get-NotesText($row) {
    if ($row.Notes) { return [string]$row.Notes }
    if ($row.Latest) { return 'No release notes were published, or they are not cached yet (use Re-check all).' }
    return 'No release information for this app.'
}

function Update-All($rows) {
    $todo = @($rows | Where-Object { $_.Status -eq 'UPDATE' })
    if ($todo.Count -eq 0) { Write-Host 'Nothing to update.'; return }
    foreach ($row in $todo) {
        $method = [string]$row.Methods[0]
        Write-Host ''
        $ans = Read-Host "Update $($row.App) via $($viaLabel[$method])? (y/N, q = stop)"
        if ($ans -match '^q') { break }
        if ($ans -match '^y') { Invoke-Method $row $method -Yes }
    }
}

function Add-App {
    Write-Host ''
    $src = (Read-Host '  Repo or release URL (GitHub, GitLab, Gitea/Forgejo, or an itch.io game page)').Trim()
    $def = [ordered]@{ name = '' }
    $short = ''
    if ($src -match 'github\.com/([\w.-]+)/([\w.-]+?)(?:\.git)?(?:/.*)?$' -or $src -match '^([\w.-]+)/([\w.-]+)$') {
        $def.source = 'github'
        $def.repo = "$($Matches[1])/$($Matches[2])"
        $short = $Matches[2]
    } elseif ($src -match '^https?://([\w-]+)\.itch\.io/([\w-]+)') {
        $def.source = 'itch'
        $def.url = "https://$($Matches[1]).itch.io/$($Matches[2])"
        $short = $Matches[2]
    } elseif ($src -match '^https?://gitlab\.com/(.+?)(?:\.git)?(?:/-/.*)?/?$') {
        $def.source = 'gitlab'
        $def.api = "https://gitlab.com/api/v4/projects/$([uri]::EscapeDataString($Matches[1]))/releases/permalink/latest"
        $short = ($Matches[1] -split '/')[-1]
    } elseif ($src -match '^https?://([^/]+)/([^/]+)/([^/]+?)(?:\.git)?(?:/.*)?$') {
        $def.source = 'gitea'
        $def.api = "https://$($Matches[1])/api/v1/repos/$($Matches[2])/$($Matches[3])/releases/latest"
        $short = $Matches[3]
    } else {
        Write-Host '  Cancelled: that does not look like a repo or release URL.' -ForegroundColor Yellow
        return
    }

    $name = (Read-Host "  App name (Enter = $short)").Trim()
    if (-not $name) { $name = $short }
    $def.name = $name
    if ($def.source -eq 'github' -and (Read-Host '  Track pre-releases / nightlies too? (y/N)') -match '^y') { $def.prerelease = $true }

    if ($def.source -eq 'itch') {
        Write-Host '  itch.io downloads go through its website, so this tracks a copy you already installed.'
        $exe = (Read-Host '  Path to its .exe').Trim().Trim('"')
        if (-not $exe) { Write-Host '  Cancelled: an itch.io app needs the path to its .exe.' -ForegroundColor Yellow; return }
        $def.exe = $exe
        $def.update = @('page')
        $script:apps = @($script:apps) + ([pscustomobject]$def)
        ConvertTo-Json -InputObject @($script:apps) -Depth 6 | Set-Content -LiteralPath $Config -Encoding UTF8
        Write-Host "  Added $name. Updates open its itch.io page." -ForegroundColor Green
        return
    }
    $exe = (Read-Host '  Path to its .exe (or a mod''s folder) if it is already installed (Enter = install it now)').Trim().Trim('"')
    if ($exe -and (Test-Path -LiteralPath $exe -PathType Container)) {
        # an installed mod or data pack: track the folder by release
        try { $latest = Get-LatestRelease ([pscustomobject]$def) }
        catch { Write-Host "  Could not read the releases: $($_.Exception.Message)" -ForegroundColor Red; return }
        $all = @(Get-ChildItem -LiteralPath $exe -Recurse -File | Sort-Object FullName)
        if ($all.Count -eq 0) { Write-Host '  That folder is empty, so I am not adding it.' -ForegroundColor Red; return }
        $ips = @($all | Where-Object { $_.Extension -ieq '.ips' })
        $def.exe = if ($ips.Count) { $ips[0].FullName } else { $all[0].FullName }
        $def.installDir = $exe
        $same = Read-Host "  Is the latest release ($($latest.Tag)) the one in that folder? (Y/n)"
        $def.installedTag = if ($same -match '^n') { 'unknown' } else { [string]$latest.Tag }
        $def.update = @('zip', 'page')
    } elseif ($exe) {
        $def.exe = $exe
        $def.update = @('download', 'page')
    } else {
        try { $latest = Get-LatestRelease ([pscustomobject]$def) }
        catch { Write-Host "  Could not read the releases: $($_.Exception.Message)" -ForegroundColor Red; return }
        Write-Host "  Latest release: $($latest.Tag)" -ForegroundColor Cyan

        $dir = (Read-Host "  Install folder (Enter = $InstallRoot\$name)").Trim().Trim('"')
        if (-not $dir) { $dir = Join-Path $InstallRoot $name }
        if ((Test-Path -LiteralPath $dir) -and @(Get-ChildItem -LiteralPath $dir -Force).Count -gt 0) {
            if ((Read-Host "  $dir already has files in it. Install into it anyway? (y/N)") -notmatch '^y') { Write-Host '  Cancelled.'; return }
        }

        # offer only files that could be a Windows build
        $winFilter = '(?i)^(?!.*(linux|appimage|macos|darwin|android|\.sha256|\.zsync)).*\.(zip|7z|exe|msi)$'
        $pick = [pscustomobject]@{ Assets = $latest.Assets; Page = $latest.Page; Def = [pscustomobject]@{ asset = $winFilter } }
        $file = Get-AssetFile $pick
        if (-not $file) { return }

        if (Test-Archive $file) {
            Expand-ToFolder $file $dir
            $method = 'zip'
        } else {
            if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
            Copy-Item -LiteralPath $file -Destination $dir -Force
            $method = 'download'
        }
        Write-Host "  Installed into $dir" -ForegroundColor Green

        # choose the file whose date/version tells us what is installed
        $cands = @(Get-ChildItem -LiteralPath $dir -Recurse -File -Filter *.exe)
        $isData = $false
        if ($cands.Count -eq 0) {
            $all = @(Get-ChildItem -LiteralPath $dir -Recurse -File | Sort-Object FullName)
            if ($all.Count -eq 0) { Write-Host '  Nothing was unpacked, so I am not adding it.' -ForegroundColor Red; return }
            if ((Read-Host '  There is no .exe in it. Is it a mod or data pack, to track by release? (Y/n)') -notmatch '^n') {
                # nothing to read a version from: remember the release instead, and one of its
                # files so a missing install still shows up
                $isData = $true
                $ips = @($all | Where-Object { $_.Extension -ieq '.ips' })
                $cands = @(if ($ips.Count) { $ips[0] } else { $all[0] })
            } else {
                $cands = @(Get-ChildItem -LiteralPath $dir -File)
                if ($cands.Count -eq 0) { Write-Host '  No program file at the top of that folder, so I am not adding it.' -ForegroundColor Red; return }
            }
        }
        $main = $cands[0]
        if ($cands.Count -gt 1) {
            Write-Host '  Which file is the main program?'
            for ($i = 0; $i -lt $cands.Count; $i++) { Write-Host ("   {0,2}  {1}" -f ($i + 1), $cands[$i].FullName.Substring($dir.Length).TrimStart('\')) }
            $n = Read-Host '  number (Enter = 1)'
            if ($n -match '^\d+$' -and [int]$n -ge 1 -and [int]$n -le $cands.Count) { $main = $cands[[int]$n - 1] }
        }
        $def.exe = $main.FullName
        Start-Sleep -Seconds 2
        if (-not (Test-Path -LiteralPath $def.exe)) {
            Show-QuarantineHelp $pick ([string]$main.Name)
            Write-Host "  Not adding $name." -ForegroundColor Red
            return
        }

        # remember which release file to fetch next time, with the version number wildcarded
        $pattern = '^' + [regex]::Escape([IO.Path]::GetFileName($file)) + '$'
        $ver = ([string]$latest.Tag) -replace '^[^\d]*', ''
        if ($ver) { $pattern = $pattern.Replace([regex]::Escape($ver), '.+') }
        $def.asset = $pattern
        $def.update = @($method, 'page')
        if ($isData) {
            $def.installDir = $dir
            $def.installedTag = [string]$latest.Tag
            Write-Host "  Tracking it by release: $($latest.Tag) is installed. Wintainium flags the next one." -ForegroundColor Cyan
        }
    }

    $script:apps = @($script:apps) + ([pscustomobject]$def)
    ConvertTo-Json -InputObject @($script:apps) -Depth 6 | Set-Content -LiteralPath $Config -Encoding UTF8
    Write-Host "  Added $name. Edit wintainium-apps.json to change how it updates." -ForegroundColor Green
}

# ---------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $Config)) {
    $example = Join-Path $script:Here 'wintainium-apps.example.json'
    Write-Host "No app list yet. Starting an empty one at $Config" -ForegroundColor Yellow
    Write-Host "(Use 'add app', or copy entries from $example.)" -ForegroundColor Yellow
    '[]' | Set-Content -LiteralPath $Config -Encoding UTF8
}
# Windows PowerShell 5.1 hands back the JSON array as one object, so unroll it explicitly.
$loaded = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
$script:apps = @($loaded | ForEach-Object { $_ })

if ($Command -eq 'token') { Set-StoredToken; return }

# ===========================================================================
#  Desktop shortcut
# ===========================================================================
if ($Command -eq 'shortcut') {
    $lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Wintainium.lnk'
    $sh = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
    $sh.TargetPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    $extra = ''
    if ($PSBoundParameters.ContainsKey('InstallRoot')) { $extra += " -InstallRoot `"$InstallRoot`"" }
    if ($PSBoundParameters.ContainsKey('Config')) { $extra += " -Config `"$Config`"" }
    $sh.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$PSCommandPath`" gui$extra"
    $sh.WorkingDirectory = $script:Here
    $ico = Join-Path $script:Here 'assets\icon.ico'
    if (Test-Path -LiteralPath $ico) { $sh.IconLocation = "$ico,0" }
    else { $sh.IconLocation = "$env:SystemRoot\System32\shell32.dll,12" }
    $sh.Save()
    Write-Host "Created $lnk" -ForegroundColor Green
    return
}

# ===========================================================================
#  Window (WPF). Reuses every function above: Write-Host and Read-Host are
#  redirected into the window's log and into dialog boxes.
# ===========================================================================
if ($Command -eq 'gui') {
    if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
        # PowerShell 7 runs MTA; WPF needs Windows PowerShell's STA thread, so restart there (hidden)
        $extra = ''
        if ($PSBoundParameters.ContainsKey('InstallRoot')) { $extra += " -InstallRoot `"$InstallRoot`"" }
        if ($PSBoundParameters.ContainsKey('Config')) { $extra += " -Config `"$Config`"" }
        Start-Process powershell.exe -WindowStyle Hidden -ArgumentList "-STA -NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" gui$extra"
        return
    }
    Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

    $styles = @'
    <Style TargetType="Button" x:Key="Btn">
      <Setter Property="Background" Value="#313244"/>
      <Setter Property="Foreground" Value="#CDD6F4"/>
      <Setter Property="BorderThickness" Value="0"/>
      <Setter Property="Padding" Value="12,7"/>
      <Setter Property="Margin" Value="0,0,8,8"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border x:Name="b" Background="{TemplateBinding Background}" CornerRadius="6" Padding="{TemplateBinding Padding}">
              <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Opacity" Value="0.82"/></Trigger>
              <Trigger Property="IsEnabled" Value="False"><Setter TargetName="b" Property="Opacity" Value="0.4"/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style TargetType="Button" x:Key="Primary" BasedOn="{StaticResource Btn}">
      <Setter Property="Background" Value="#CBA6F7"/>
      <Setter Property="Foreground" Value="#1E1E2E"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
    </Style>
'@

    $mainXaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Wintainium" Width="1120" Height="740" MinWidth="820" MinHeight="480"
        WindowStartupLocation="CenterScreen" Background="#1E1E2E" Foreground="#CDD6F4"
        FontFamily="Segoe UI" FontSize="13">
  <Window.Resources>
STYLES
  </Window.Resources>
  <Grid>
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="*"/>
      <RowDefinition Height="170"/>
      <RowDefinition Height="Auto"/>
    </Grid.RowDefinitions>

    <StackPanel Grid.Row="0" Margin="20,16,20,6">
      <TextBlock Text="Wintainium" FontSize="22" FontWeight="SemiBold"/>
      <TextBlock x:Name="Summary" Foreground="#A6ADC8" Margin="0,2,0,0" Text="Checking..."/>
    </StackPanel>

    <WrapPanel Grid.Row="1" Margin="20,8,20,4">
      <Button x:Name="BtnUpdate" Style="{StaticResource Primary}">
        <StackPanel Orientation="Horizontal"><TextBlock FontFamily="Segoe MDL2 Assets" Text="&#xE896;" Margin="0,1,8,0"/><TextBlock Text="Update selected"/></StackPanel>
      </Button>
      <Button x:Name="BtnAll" Style="{StaticResource Btn}">
        <StackPanel Orientation="Horizontal"><TextBlock FontFamily="Segoe MDL2 Assets" Text="&#xE895;" Margin="0,1,8,0"/><TextBlock Text="Update all"/></StackPanel>
      </Button>
      <Button x:Name="BtnNotes" Style="{StaticResource Btn}">
        <StackPanel Orientation="Horizontal"><TextBlock FontFamily="Segoe MDL2 Assets" Text="&#xE8A5;" Margin="0,1,8,0"/><TextBlock Text="What's new"/></StackPanel>
      </Button>
      <Button x:Name="BtnLaunch" Style="{StaticResource Btn}">
        <StackPanel Orientation="Horizontal"><TextBlock FontFamily="Segoe MDL2 Assets" Text="&#xE768;" Margin="0,1,8,0"/><TextBlock Text="Launch"/></StackPanel>
      </Button>
      <Button x:Name="BtnPage" Style="{StaticResource Btn}">
        <StackPanel Orientation="Horizontal"><TextBlock FontFamily="Segoe MDL2 Assets" Text="&#xE8A7;" Margin="0,1,8,0"/><TextBlock Text="Release page"/></StackPanel>
      </Button>
      <Button x:Name="BtnFolder" Style="{StaticResource Btn}">
        <StackPanel Orientation="Horizontal"><TextBlock FontFamily="Segoe MDL2 Assets" Text="&#xE838;" Margin="0,1,8,0"/><TextBlock Text="Open folder"/></StackPanel>
      </Button>
      <Button x:Name="BtnAdd" Style="{StaticResource Btn}">
        <StackPanel Orientation="Horizontal"><TextBlock FontFamily="Segoe MDL2 Assets" Text="&#xE710;" Margin="0,1,8,0"/><TextBlock Text="Add app"/></StackPanel>
      </Button>
      <Button x:Name="BtnRefresh" Style="{StaticResource Btn}">
        <StackPanel Orientation="Horizontal"><TextBlock FontFamily="Segoe MDL2 Assets" Text="&#xE72C;" Margin="0,1,8,0"/><TextBlock Text="Refresh"/></StackPanel>
      </Button>
      <Button x:Name="BtnForce" Style="{StaticResource Btn}" ToolTip="Ignore the 60-minute cache and ask every release source again">
        <TextBlock Text="Re-check all"/>
      </Button>
    </WrapPanel>

    <DockPanel Grid.Row="2" Margin="20,0,20,8" LastChildFill="False">
      <Grid DockPanel.Dock="Left" Width="300">
        <TextBox x:Name="Search" Background="#313244" Foreground="#CDD6F4" BorderBrush="#45475A"
                 Padding="8,5" CaretBrush="#CDD6F4" VerticalContentAlignment="Center"/>
        <TextBlock x:Name="SearchHint" Text="Search apps..." Foreground="#6C7086" Margin="11,0,0,0"
                   VerticalAlignment="Center" IsHitTestVisible="False"/>
      </Grid>
      <CheckBox x:Name="OnlyUpdates" DockPanel.Dock="Left" Content="Only show updates" Foreground="#CDD6F4"
                VerticalAlignment="Center" Margin="16,0,0,0"/>
      <TextBlock DockPanel.Dock="Right" Foreground="#6C7086" VerticalAlignment="Center"
                 Text="Double-click to update  -  right-click for more  -  F5 to refresh"/>
    </DockPanel>

    <DataGrid x:Name="Grid" Grid.Row="3" Margin="20,0,20,10" AutoGenerateColumns="False" IsReadOnly="True"
              SelectionMode="Single" SelectionUnit="FullRow" HeadersVisibility="Column" GridLinesVisibility="None"
              Background="#181825" RowBackground="#1E1E2E" AlternatingRowBackground="#232336"
              BorderThickness="0" Foreground="#CDD6F4" CanUserAddRows="False" CanUserResizeRows="False"
              RowHeight="34" FocusVisualStyle="{x:Null}">
      <DataGrid.ColumnHeaderStyle>
        <Style TargetType="DataGridColumnHeader">
          <Setter Property="Background" Value="#181825"/>
          <Setter Property="Foreground" Value="#A6ADC8"/>
          <Setter Property="Padding" Value="10,9"/>
          <Setter Property="BorderThickness" Value="0"/>
          <Setter Property="FontWeight" Value="SemiBold"/>
        </Style>
      </DataGrid.ColumnHeaderStyle>
      <DataGrid.CellStyle>
        <Style TargetType="DataGridCell">
          <Setter Property="BorderThickness" Value="0"/>
          <Setter Property="Padding" Value="10,0"/>
          <Setter Property="Foreground" Value="#CDD6F4"/>
          <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="DataGridCell">
                <Border Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}">
                  <ContentPresenter VerticalAlignment="Center"/>
                </Border>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
          <Style.Triggers>
            <Trigger Property="IsSelected" Value="True">
              <Setter Property="Background" Value="#45475A"/>
              <Setter Property="Foreground" Value="#FFFFFF"/>
            </Trigger>
          </Style.Triggers>
        </Style>
      </DataGrid.CellStyle>
      <DataGrid.Columns>
        <DataGridTextColumn Header="App" Binding="{Binding App}" Width="2*"/>
        <DataGridTemplateColumn Header="Status" Width="140" SortMemberPath="Status">
          <DataGridTemplateColumn.CellTemplate>
            <DataTemplate>
              <Border x:Name="pill" CornerRadius="10" Padding="10,2" HorizontalAlignment="Left" Background="#313244">
                <TextBlock x:Name="pt" Text="{Binding Status}" FontSize="12" Foreground="#A6ADC8"/>
              </Border>
              <DataTemplate.Triggers>
                <DataTrigger Binding="{Binding Status}" Value="UPDATE">
                  <Setter TargetName="pill" Property="Background" Value="#F9E2AF"/>
                  <Setter TargetName="pt" Property="Foreground" Value="#1E1E2E"/>
                  <Setter TargetName="pt" Property="FontWeight" Value="SemiBold"/>
                </DataTrigger>
                <DataTrigger Binding="{Binding Status}" Value="up to date">
                  <Setter TargetName="pill" Property="Background" Value="#26352B"/>
                  <Setter TargetName="pt" Property="Foreground" Value="#A6E3A1"/>
                </DataTrigger>
                <DataTrigger Binding="{Binding Status}" Value="lookup failed">
                  <Setter TargetName="pill" Property="Background" Value="#3B2530"/>
                  <Setter TargetName="pt" Property="Foreground" Value="#F38BA8"/>
                </DataTrigger>
                <DataTrigger Binding="{Binding Status}" Value="rate limited">
                  <Setter TargetName="pill" Property="Background" Value="#3B2530"/>
                  <Setter TargetName="pt" Property="Foreground" Value="#F38BA8"/>
                </DataTrigger>
                <DataTrigger Binding="{Binding Status}" Value="token rejected">
                  <Setter TargetName="pill" Property="Background" Value="#3B2530"/>
                  <Setter TargetName="pt" Property="Foreground" Value="#F38BA8"/>
                </DataTrigger>
                <DataTrigger Binding="{Binding Status}" Value="exe not found">
                  <Setter TargetName="pill" Property="Background" Value="#3B2530"/>
                  <Setter TargetName="pt" Property="Foreground" Value="#F38BA8"/>
                </DataTrigger>
              </DataTemplate.Triggers>
            </DataTemplate>
          </DataGridTemplateColumn.CellTemplate>
        </DataGridTemplateColumn>
        <DataGridTextColumn Header="Installed" Binding="{Binding Installed}" Width="1.2*"/>
        <DataGridTextColumn Header="Latest" Binding="{Binding Latest}" Width="1.5*">
          <DataGridTextColumn.ElementStyle>
            <Style TargetType="TextBlock">
              <Setter Property="TextTrimming" Value="CharacterEllipsis"/>
              <Setter Property="ToolTip" Value="{Binding Latest}"/>
            </Style>
          </DataGridTextColumn.ElementStyle>
        </DataGridTextColumn>
        <DataGridTextColumn Header="Released" Binding="{Binding Released}" Width="100"/>
        <DataGridTextColumn Header="Update via" Binding="{Binding Via}" Width="110"/>
      </DataGrid.Columns>
    </DataGrid>

    <Border Grid.Row="4" Margin="20,0,20,12" Background="#181825" CornerRadius="8" Padding="10,6">
      <ScrollViewer x:Name="LogScroll" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
        <ItemsControl x:Name="Log">
          <ItemsControl.ItemTemplate>
            <DataTemplate>
              <TextBlock Text="{Binding Text}" Foreground="{Binding Brush}" TextWrapping="Wrap"
                         FontFamily="Cascadia Mono, Consolas" FontSize="12" Margin="0,1"/>
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
      </ScrollViewer>
    </Border>

    <Border Grid.Row="5" Background="#11111B" Padding="20,6">
      <TextBlock x:Name="Status" Foreground="#A6ADC8" Text="Ready"/>
    </Border>
  </Grid>
</Window>
'@

    $dialogXaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Wintainium" Width="560" SizeToContent="Height" ResizeMode="NoResize"
        WindowStartupLocation="CenterOwner" Background="#1E1E2E" Foreground="#CDD6F4"
        FontFamily="Segoe UI" FontSize="13" ShowInTaskbar="False">
  <Window.Resources>
STYLES
  </Window.Resources>
  <StackPanel Margin="20,18,20,12">
    <TextBlock x:Name="Ctx" FontFamily="Cascadia Mono, Consolas" FontSize="12" Foreground="#A6ADC8"
               TextWrapping="Wrap" Margin="0,0,0,12"/>
    <TextBlock x:Name="Prompt" TextWrapping="Wrap" FontWeight="SemiBold" FontSize="14" Margin="0,0,0,10"/>
    <TextBox x:Name="Input" Background="#313244" Foreground="#CDD6F4" BorderBrush="#45475A"
             Padding="6,5" CaretBrush="#CDD6F4"/>
    <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,16,0,0">
      <Button x:Name="B1" Style="{StaticResource Primary}"/>
      <Button x:Name="B2" Style="{StaticResource Btn}"/>
      <Button x:Name="B3" Style="{StaticResource Btn}"/>
    </StackPanel>
  </StackPanel>
</Window>
'@

    $notesXaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="What's new" Width="720" Height="560" WindowStartupLocation="CenterOwner"
        Background="#1E1E2E" Foreground="#CDD6F4" FontFamily="Segoe UI" FontSize="13" ShowInTaskbar="False">
  <Window.Resources>
STYLES
  </Window.Resources>
  <Grid Margin="20,16,20,12">
    <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
    <StackPanel Grid.Row="0" Margin="0,0,0,10">
      <TextBlock x:Name="NTitle" FontSize="18" FontWeight="SemiBold"/>
      <TextBlock x:Name="NSub" Foreground="#A6ADC8" Margin="0,2,0,0"/>
    </StackPanel>
    <TextBox x:Name="NBody" Grid.Row="1" IsReadOnly="True" TextWrapping="Wrap" VerticalScrollBarVisibility="Auto"
             Background="#181825" Foreground="#CDD6F4" BorderThickness="0" Padding="10"
             FontFamily="Cascadia Mono, Consolas" FontSize="12"/>
    <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,12,0,0">
      <Button x:Name="NOpen" Style="{StaticResource Btn}" Content="Open release page"/>
      <Button x:Name="NClose" Style="{StaticResource Primary}" Content="Close" IsCancel="True"/>
    </StackPanel>
  </Grid>
</Window>
'@

    function New-Xaml([string]$text) {
        $x = [xml]($text.Replace('STYLES', $styles))
        return [Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $x))
    }

    $script:win = New-Xaml $mainXaml
    $script:win.Title = "Wintainium $script:AppVersion"
    $ico = Join-Path $script:Here 'assets\icon.ico'
    if (Test-Path -LiteralPath $ico) {
        try { $script:win.Icon = [System.Windows.Media.Imaging.BitmapFrame]::Create([uri]$ico) } catch { Write-Verbose 'icon not loaded' }
    }
    $script:ui = @{}
    foreach ($n in 'Summary', 'Grid', 'Log', 'LogScroll', 'Status', 'Search', 'SearchHint', 'OnlyUpdates', 'BtnUpdate', 'BtnAll', 'BtnNotes', 'BtnLaunch', 'BtnPage', 'BtnFolder', 'BtnAdd', 'BtnRefresh', 'BtnForce') {
        $script:ui[$n] = $script:win.FindName($n)
    }
    $script:logItems = New-Object System.Collections.ObjectModel.ObservableCollection[object]
    $script:ui.Log.ItemsSource = $script:logItems
    $script:promptContext = New-Object System.Collections.Generic.List[string]
    $script:busy = $false

    # Let the window repaint while long work runs on the same thread.
    function Invoke-UiPump {
        $frame = New-Object System.Windows.Threading.DispatcherFrame
        $cb = [System.Windows.Threading.DispatcherOperationCallback] { param($f) $f.Continue = $false; return $null }
        [void][System.Windows.Threading.Dispatcher]::CurrentDispatcher.BeginInvoke([System.Windows.Threading.DispatcherPriority]::Background, $cb, $frame)
        [System.Windows.Threading.Dispatcher]::PushFrame($frame)
    }

    $script:colorMap = @{
        Yellow = '#F9E2AF'; Red = '#F38BA8'; Green = '#A6E3A1'; Cyan = '#89DCEB'
        DarkCyan = '#74C7EC'; DarkGray = '#6C7086'; Gray = '#BAC2DE'; White = '#FFFFFF'
    }

    # Everything the functions above print lands in the log pane (or the status bar for progress lines).
    function Write-Host {
        param([Parameter(Position = 0, ValueFromRemainingArguments = $true)]$Object,
              [switch]$NoNewline, $ForegroundColor, $BackgroundColor, $Separator)
        $text = (@($Object) | ForEach-Object { "$_" }) -join ' '
        if ($text.StartsWith("`r")) {
            $script:ui.Status.Text = $text.Trim()
            Invoke-UiPump
            return
        }
        if (-not $text.Trim()) { return }
        $brush = '#CDD6F4'
        if ($ForegroundColor -and $script:colorMap.ContainsKey([string]$ForegroundColor)) { $brush = $script:colorMap[[string]$ForegroundColor] }
        $script:logItems.Add([pscustomobject]@{ Text = $text.TrimEnd(); Brush = $brush })
        $script:promptContext.Add($text.TrimEnd())
        $script:ui.LogScroll.ScrollToEnd()
        Invoke-UiPump
    }

    # Every question the functions above ask becomes a dialog box.
    function Show-Prompt([string]$prompt) {
        $ctx = @($script:promptContext | Select-Object -Last 12) -join "`n"
        $script:promptContext.Clear()
        $script:dlg = New-Xaml $dialogXaml
        $script:dlg.Owner = $script:win
        $script:dlgResult = ''
        $d = @{}
        foreach ($n in 'Ctx', 'Prompt', 'Input', 'B1', 'B2', 'B3') { $d[$n] = $script:dlg.FindName($n) }
        $d.Ctx.Text = $ctx
        if (-not $ctx) { $d.Ctx.Visibility = 'Collapsed' }

        if ($prompt -match '\((y/N|Y/n)') {
            $d.Prompt.Text = ($prompt -replace '\s*\((y/N|Y/n)[^)]*\)\s*:?\s*$', '').Trim()
            $d.Input.Visibility = 'Collapsed'
            $d.B1.Content = 'Yes'; $d.B1.Add_Click({ $script:dlgResult = 'y'; $script:dlg.Close() })
            $d.B2.Content = 'No';  $d.B2.IsCancel = $true; $d.B2.Add_Click({ $script:dlgResult = 'n'; $script:dlg.Close() })
            if ($prompt -match 'q = stop') {
                $d.B3.Content = 'Stop'; $d.B3.Add_Click({ $script:dlgResult = 'q'; $script:dlg.Close() })
            } else { $d.B3.Visibility = 'Collapsed' }
        } else {
            $d.Prompt.Text = $prompt.Trim()
            $d.B1.Content = 'OK'; $d.B1.IsDefault = $true
            $d.B1.Add_Click({ $script:dlgResult = $script:dlg.FindName('Input').Text; $script:dlg.Close() })
            $d.B2.Content = 'Cancel'; $d.B2.IsCancel = $true; $d.B2.Add_Click({ $script:dlgResult = ''; $script:dlg.Close() })
            $d.B3.Visibility = 'Collapsed'
            $script:dlg.Add_ContentRendered({ [void]$script:dlg.FindName('Input').Focus() })
        }
        [void]$script:dlg.ShowDialog()
        return [string]$script:dlgResult
    }

    function Read-Host {
        param([Parameter(Position = 0)]$Prompt, [switch]$AsSecureString)
        return (Show-Prompt ([string]$Prompt))
    }

    function Set-Busy([bool]$on, [string]$label) {
        $script:busy = $on
        foreach ($n in 'BtnUpdate', 'BtnAll', 'BtnNotes', 'BtnLaunch', 'BtnPage', 'BtnFolder', 'BtnAdd', 'BtnRefresh', 'BtnForce') { $script:ui[$n].IsEnabled = -not $on }
        $script:ui.Status.Text = if ($on) { $label } else { 'Ready' }
        Invoke-UiPump
    }

    function Invoke-Busy([string]$label, [scriptblock]$action) {
        if ($script:busy) { return }
        Set-Busy $true $label
        try { & $action }
        catch { Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red }
        finally { Set-Busy $false '' }
    }

    function Update-Grid([switch]$Force) {
        $keep = if ($script:ui.Grid.SelectedItem) { $script:ui.Grid.SelectedItem.App } else { $null }
        $script:rows = @(Get-AppRows -Force:$Force)
        $script:ui.Grid.ItemsSource = $script:rows
        $view = [System.Windows.Data.CollectionViewSource]::GetDefaultView($script:ui.Grid.ItemsSource)
        $view.Filter = [Predicate[object]] { param($o) Test-RowVisible $o }
        if ($keep) { $script:ui.Grid.SelectedItem = $script:rows | Where-Object { $_.App -eq $keep } | Select-Object -First 1 }
        $n = @($script:rows | Where-Object { $_.Status -eq 'UPDATE' }).Count
        $bad = @($script:rows | Where-Object { $_.Status -notin 'UPDATE', 'up to date', 'check manually' }).Count
        $when = "checked $(Get-Date -Format 'HH:mm')"
        if ($n -eq 0 -and $bad -eq 0) {
            $script:ui.Summary.Text = "All $($script:rows.Count) apps are up to date  -  $when"
            $script:ui.Summary.Foreground = '#A6E3A1'
        } else {
            $parts = @()
            if ($n) { $parts += "$n update$(if ($n -ne 1) { 's' }) available" }
            if ($bad) { $parts += "$bad need$(if ($bad -eq 1) { 's' }) attention" }
            $script:ui.Summary.Text = (($parts + "$($script:rows.Count) apps", $when) -join '  -  ')
            $script:ui.Summary.Foreground = if ($n) { '#F9E2AF' } else { '#F38BA8' }
        }
        if (@($script:rows | Where-Object { $_.Status -eq 'rate limited' }).Count -gt 0) {
            Write-Host 'Some lookups hit GitHub''s hourly limit. Run ".\Wintainium.ps1 token" once to save a token.' -ForegroundColor Yellow
        }
        if (@($script:rows | Where-Object { $_.Status -eq 'token rejected' }).Count -gt 0) {
            Write-Host 'GitHub rejected the saved token (expired or revoked). Make a new one and run ".\Wintainium.ps1 token".' -ForegroundColor Yellow
        }
    }

    function Test-RowVisible($o) {
        if ($script:ui.OnlyUpdates.IsChecked -and $o.Status -eq 'up to date') { return $false }
        $q = [string]$script:ui.Search.Text
        if ($q.Trim() -and ([string]$o.App).IndexOf($q.Trim(), [StringComparison]::OrdinalIgnoreCase) -lt 0) { return $false }
        return $true
    }

    function Update-Filter {
        $script:ui.SearchHint.Visibility = if ($script:ui.Search.Text) { 'Collapsed' } else { 'Visible' }
        if ($script:ui.Grid.ItemsSource) { [System.Windows.Data.CollectionViewSource]::GetDefaultView($script:ui.Grid.ItemsSource).Refresh() }
    }

    function Show-Notes($r) {
        $script:notesWin = New-Xaml $notesXaml
        $script:notesWin.Owner = $script:win
        $script:notesRow = $r
        $script:notesWin.FindName('NTitle').Text = "$($r.App)  $($r.Latest)"
        $script:notesWin.FindName('NSub').Text = if ($r.Released) { "Released $($r.Released)  -  you have $($r.Installed)" } else { "You have $($r.Installed)" }
        $text = (Get-NotesText $r) -replace "`r`n", "`n" -replace '\*\*', '' -replace '(?m)^#+\s*', ''
        $script:notesWin.FindName('NBody').Text = $text
        $script:notesWin.FindName('NOpen').Add_Click({ if ($script:notesRow.Page) { Start-Process $script:notesRow.Page } })
        $script:notesWin.FindName('NClose').Add_Click({ $script:notesWin.Close() })
        [void]$script:notesWin.ShowDialog()
    }

    function Get-Selected {
        $r = $script:ui.Grid.SelectedItem
        if (-not $r) { Write-Host 'Select an app in the list first.' -ForegroundColor Yellow }
        return $r
    }

    function Start-SelectedUpdate {
        $r = Get-Selected
        if (-not $r) { return }
        Invoke-Busy "Updating $($r.App)..." {
            $go = $true
            if ($r.Status -ne 'UPDATE') {
                $go = (Read-Host "$($r.App) is $($r.Status). Run its update anyway? (y/N)") -match '^y'
            }
            if ($go) { Update-App $r; Update-Grid }
        }
    }

    $script:ui.BtnUpdate.Add_Click({ Start-SelectedUpdate })
    $script:ui.Grid.Add_MouseDoubleClick({ if ($script:ui.Grid.SelectedItem) { Start-SelectedUpdate } })
    $script:ui.BtnAll.Add_Click({ Invoke-Busy 'Updating all outdated apps...' { Update-All $script:rows; Update-Grid } })
    $script:ui.BtnPage.Add_Click({
        $r = Get-Selected
        if ($r) { if ($r.Page) { Start-Process $r.Page } else { Write-Host "No release page is set for $($r.App)." -ForegroundColor Yellow } }
    })
    $script:ui.BtnFolder.Add_Click({
        $r = Get-Selected
        if ($r) {
            $dir = Get-AppFolder $r
            if (Test-Path -LiteralPath $dir) { Start-Process explorer.exe "`"$dir`"" }
            else { Write-Host "Folder not found: $dir" -ForegroundColor Red }
        }
    })
    $script:ui.BtnAdd.Add_Click({ Invoke-Busy 'Adding an app...' { Add-App; Update-Grid } })
    $script:ui.BtnNotes.Add_Click({ $r = Get-Selected; if ($r) { Show-Notes $r } })
    $script:ui.BtnLaunch.Add_Click({ $r = Get-Selected; if ($r) { Start-App $r } })
    $script:ui.Search.Add_TextChanged({ Update-Filter })
    $script:ui.OnlyUpdates.Add_Checked({ Update-Filter })
    $script:ui.OnlyUpdates.Add_Unchecked({ Update-Filter })

    # right-click selects the row under the mouse, then shows the menu
    $script:ui.Grid.Add_PreviewMouseRightButtonDown({
        param($src, $e)
        $d = $e.OriginalSource
        while ($d -and -not ($d -is [System.Windows.Controls.DataGridRow])) {
            $d = if ($d -is [System.Windows.Media.Visual] -or $d -is [System.Windows.Media.Media3D.Visual3D]) { [System.Windows.Media.VisualTreeHelper]::GetParent($d) } else { $null }
        }
        if ($d) { $d.IsSelected = $true }
    })
    $menu = New-Object System.Windows.Controls.ContextMenu
    foreach ($item in @(
            @{ h = 'Update'; a = { Start-SelectedUpdate } },
            @{ h = "What's new"; a = { $r = Get-Selected; if ($r) { Show-Notes $r } } },
            @{ h = 'Launch'; a = { $r = Get-Selected; if ($r) { Start-App $r } } },
            @{ h = '-' },
            @{ h = 'Open release page'; a = { $r = Get-Selected; if ($r -and $r.Page) { Start-Process $r.Page } } },
            @{ h = 'Open folder'; a = { $r = Get-Selected; if ($r) { $dir = Get-AppFolder $r; if (Test-Path -LiteralPath $dir) { Start-Process explorer.exe "`"$dir`"" } } } },
            @{ h = '-' },
            @{ h = 'Remove from list...'; a = { $r = Get-Selected; if ($r) { Invoke-Busy 'Removing...' { if (Remove-AppEntry $r) { Update-Grid } } } } })) {
        if ($item.h -eq '-') { [void]$menu.Items.Add((New-Object System.Windows.Controls.Separator)); continue }
        $mi = New-Object System.Windows.Controls.MenuItem
        $mi.Header = $item.h
        $mi.Add_Click($item.a)
        [void]$menu.Items.Add($mi)
    }
    $script:ui.Grid.ContextMenu = $menu

    $script:win.Add_KeyDown({
        param($src, $e)
        if ($e.Key -eq 'F5') { Invoke-Busy 'Checking...' { Update-Grid } }
        elseif ($e.Key -eq 'Delete' -and $script:ui.Grid.IsKeyboardFocusWithin) {
            $r = Get-Selected; if ($r) { Invoke-Busy 'Removing...' { if (Remove-AppEntry $r) { Update-Grid } } }
        }
    })
    $script:ui.BtnRefresh.Add_Click({ Invoke-Busy 'Checking...' { Update-Grid } })
    $script:ui.BtnForce.Add_Click({ Invoke-Busy 'Re-checking every release source...' { Update-Grid -Force } })
    $script:win.Add_ContentRendered({ Invoke-Busy 'Checking...' { Update-Grid } })

    [void]$script:win.ShowDialog()
    return
}


Write-Host "Wintainium $script:AppVersion" -ForegroundColor Cyan
$rows = @(Get-AppRows)
Show-Table $rows
if ($Command -eq 'list') { return }

while ($true) {
    Write-Host ''
    Write-Host '[number] update   a = all outdated   w [n] = what''s new   l [n] = launch   o [n] = release page   n = add app   x [n] = remove   r = refresh   rf = re-check all   q = quit' -ForegroundColor DarkCyan
    $in = (Read-Host 'wintainium').Trim()
    if ($in -match '^q') { break }
    elseif ($in -match '^rf$') { $rows = @(Get-AppRows -Force); Show-Table $rows }
    elseif ($in -match '^r$') { $rows = @(Get-AppRows); Show-Table $rows }
    elseif ($in -match '^a$') { Update-All $rows; $rows = @(Get-AppRows); Show-Table $rows }
    elseif ($in -match '^n$') { Add-App; $rows = @(Get-AppRows); Show-Table $rows }
    elseif ($in -match '^(l|w|x)\s*(\d+)$' -and [int]$Matches[2] -ge 1 -and [int]$Matches[2] -le $rows.Count) {
        $r = $rows[[int]$Matches[2] - 1]
        switch ($Matches[1]) {
            'l' { Start-App $r }
            'w' { Write-Host ''; Write-Host "$($r.App) $($r.Latest)" -ForegroundColor Cyan; Write-Host (Get-NotesText $r) }
            'x' { if (Remove-AppEntry $r) { $rows = @(Get-AppRows); Show-Table $rows } }
        }
    }
    elseif ($in -match '^o\s*(\d+)$' -and [int]$Matches[1] -ge 1 -and [int]$Matches[1] -le $rows.Count) {
        $p = $rows[[int]$Matches[1] - 1].Page
        if ($p) { Start-Process $p } else { Write-Host '  No release page for that app.' }
    }
    elseif ($in -match '^\d+$' -and [int]$in -ge 1 -and [int]$in -le $rows.Count) { Update-App $rows[[int]$in - 1] }
    elseif ($in) { Write-Host '  Not a command.' }
}
