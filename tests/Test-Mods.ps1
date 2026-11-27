# Tests mods and data packs (no .exe inside): add-app install, tracking an existing folder,
# status by release tag, zip updates recording the new tag, and Launch/Open folder.
param([string]$Script = '')
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)
if (-not $Script) { $Script = Join-Path $repo 'Wintainium.ps1' }
$ErrorActionPreference = 'Stop'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($Script, [ref]$null, [ref]$null)
$fns = $ast.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.FunctionDefinitionAst] }
foreach ($f in $fns) { . ([scriptblock]::Create($f.Extent.Text)) }

$root = Join-Path ([IO.Path]::GetTempPath()) ("wintest3-" + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory $root | Out-Null
if (-not $env:TEMP) { $env:TEMP = [IO.Path]::GetTempPath() }
$DownloadDir = Join-Path $root 'dl'; $InstallRoot = Join-Path $root 'apps'
$script:sevenZip = '/nonexistent/7z.exe'; $ua = @{ 'User-Agent' = 't' }; $ghHeaders = $ua
$viaLabel = @{ emudeck='EmuDeck'; inapp='in-app'; installer='installer'; zip='zip install'; download='download'; page='release page' }
$GraceDays = 3; $CacheMinutes = 60; $script:cache = @{}
$script:AllowFileUrls = $true   # the release zips are served from disk via file:// URLs
function Save-Cache { }
$fails = 0
function Check($name, $cond) { if ($cond) { "PASS  $name" } else { "FAIL  $name"; $script:fails++ } }
function global:Start-Process { param($FilePath, $ArgumentList) $global:started += ,"$FilePath $ArgumentList" }
$global:started = @()
$global:answers = [System.Collections.Generic.Queue[string]]::new()
function global:Read-Host { param($Prompt) return $global:answers.Dequeue() }
function Answer([string[]]$a) { $global:answers.Clear(); $a | ForEach-Object { $global:answers.Enqueue($_) } }

# a mod release: one top folder (flattened on unpack) holding <mod>\patches\<file>.ips, no .exe
$srv = Join-Path $root 'srv'; New-Item -ItemType Directory $srv | Out-Null
function New-ModZip([string]$ver) {
    $stage = Join-Path $root "stage-$ver"
    New-Item -ItemType Directory "$stage/GAMEDATA/Widescreen 21x9/patches" -Force | Out-Null
    Set-Content "$stage/GAMEDATA/Widescreen 21x9/patches/0123ABCD.ips" "patch $ver"
    Set-Content "$stage/GAMEDATA/Widescreen 21x9/readme.txt" "readme $ver"
    $zip = Join-Path $srv "widescreen-21x9-$ver.zip"
    Compress-Archive -Path "$stage/GAMEDATA" -DestinationPath $zip
    $u = if ($zip -match '^[A-Za-z]:') { ([uri]$zip).AbsoluteUri } else { 'file://' + $zip }
    return [pscustomobject]@{ name = [IO.Path]::GetFileName($zip); browser_download_url = $u }
}
$assets = @{ 'v1.0' = (New-ModZip 'v1.0'); 'v1.1' = (New-ModZip 'v1.1') }
$global:relTag = 'v1.0'
function global:Invoke-RestMethod { param([Parameter(Position=0)]$Uri, $Headers)
    if ($Uri -notmatch 'api\.github\.com/repos/someone/game-mod/releases/latest') { throw "bad api $Uri" }
    [pscustomobject]@{ tag_name = $global:relTag; published_at = (Get-Date).ToUniversalTime().ToString('o')
        html_url = "https://github.com/someone/game-mod/releases/tag/$global:relTag"; body = 'notes'; assets = @($global:relAssets) } }
$global:relAssets = @($assets['v1.0'])

$Config = Join-Path $root 'apps.json'
function Saved { @((Get-Content $Config -Raw | ConvertFrom-Json) | ForEach-Object { $_ }) }

# 1. add-app installs a mod with no .exe and tracks it by release
'[]' | Set-Content $Config; $script:apps = @()
Answer 'https://github.com/someone/game-mod', 'Widescreen', 'n', '', '', ''
$out = Add-App 6>&1 | Out-String
$e = Saved | Select-Object -First 1
$modDir = Join-Path $InstallRoot 'Widescreen'
Check 'mod: added instead of "Nothing was unpacked"'  ($e -and $out -notmatch 'Nothing was unpacked')
Check 'mod: unpacked with its own folder kept'         (Test-Path (Join-Path $modDir 'Widescreen 21x9/patches/0123ABCD.ips'))
Check 'mod: tracked by the .ips file'                  ([string]$e.exe -like '*0123ABCD.ips')
Check 'mod: installDir is the install folder'          ($e.installDir -eq $modDir)
Check 'mod: installedTag recorded'                     ($e.installedTag -eq 'v1.0')
Check 'mod: updates by zip, then page'                 (@($e.update)[0] -eq 'zip' -and @($e.update)[1] -eq 'page')
Check 'mod: asset pattern matches the next version'    ('widescreen-21x9-v1.1.zip' -match $e.asset)

# 2. status: same tag = up to date, Installed shows the tag; new tag = UPDATE
$script:apps = @(Saved)
$rows = @(Get-AppRows -Force 6>$null)
Check 'mod: same release is up to date'                ($rows[0].Status -eq 'up to date')
Check 'mod: Installed column shows the release tag'    ($rows[0].Installed -eq 'v1.0')
$global:relTag = 'v1.1'; $global:relAssets = @($assets['v1.1'])
$rows = @(Get-AppRows -Force 6>$null)
Check 'mod: new release flagged UPDATE'                ($rows[0].Status -eq 'UPDATE' -and $rows[0].Latest -eq 'v1.1')

# 3. zip update unpacks into installDir (not the .ips folder), backs up, and records the new tag
Answer 'y'
$out = Invoke-Method $rows[0] 'zip' 6>&1 | Out-String
Check 'mod update: new files in place'                 ((Get-Content (Join-Path $modDir 'Widescreen 21x9/patches/0123ABCD.ips')) -eq 'patch v1.1')
Check 'mod update: nothing unpacked into patches\'     (-not (Test-Path (Join-Path $modDir 'Widescreen 21x9/patches/Widescreen 21x9')))
Check 'mod update: folder backed up first'             (@(Get-ChildItem $InstallRoot -Directory | Where-Object Name -like 'Widescreen.backup-*').Count -eq 1)
Check 'mod update: installedTag saved'                 ((Saved)[0].installedTag -eq 'v1.1')
$script:apps = @(Saved)
$rows = @(Get-AppRows -Force 6>$null)
Check 'mod update: up to date afterwards'              ($rows[0].Status -eq 'up to date')

# 4. cancelling the update keeps the old tag
$global:relTag = 'v1.2'; $global:relAssets = @($assets['v1.1'])
$rows = @(Get-AppRows -Force 6>$null)
Answer 'n'
Invoke-Method $rows[0] 'zip' 6>&1 | Out-Null
Check 'mod update cancelled: tag unchanged'            ((Saved)[0].installedTag -eq 'v1.1')

# 5. Launch on a mod opens its folder; Open folder uses installDir
$global:started = @()
Start-App $rows[0] 6>&1 | Out-Null
Check 'mod: Launch opens the folder, does not run the .ips' (($global:started -join '|') -match 'explorer\.exe' -and ($global:started -join '|') -notmatch '\.ips')
Check 'mod: Get-AppFolder returns installDir'          ((Get-AppFolder $rows[0]) -eq $modDir)

# 6. track a mod folder that is already installed
$global:relTag = 'v1.1'; $global:relAssets = @($assets['v1.1'])
'[]' | Set-Content $Config; $script:apps = @()
Answer 'https://github.com/someone/game-mod', 'Existing', 'n', "`"$modDir`"", ''
Add-App 6>&1 | Out-Null
$e = Saved | Select-Object -First 1
Check 'existing folder: tracked by release'            ($e.installedTag -eq 'v1.1' -and $e.installDir -eq $modDir -and [string]$e.exe -like '*.ips')
'[]' | Set-Content $Config; $script:apps = @()
Answer 'https://github.com/someone/game-mod', 'Unsure', 'n', $modDir, 'n'
Add-App 6>&1 | Out-Null
$script:apps = @(Saved)
$rows = @(Get-AppRows -Force 6>$null)
Check 'existing folder, unknown release: shows UPDATE' ($script:apps[0].installedTag -eq 'unknown' -and $rows[0].Status -eq 'UPDATE')

# 7. answering No keeps the old behaviour (pick a top-level file as the program)
'[]' | Set-Content $Config; $script:apps = @()
$global:relAssets = @($assets['v1.1'])
Answer 'https://github.com/someone/game-mod', 'NotAMod', 'n', '', '', 'n'
$out = Add-App 6>&1 | Out-String
Check 'not a mod: refused when no top-level program'   ($out -match 'No program file at the top' -and @(Saved).Count -eq 0)

# 8. ordinary apps are unchanged: no installedTag means status still comes from the .exe date
$exe = Join-Path $root 'plain/app.exe'; New-Item -ItemType Directory (Split-Path $exe) -Force | Out-Null
Set-Content $exe 'x'; (Get-Item $exe).LastWriteTime = (Get-Date).AddDays(-60)
$script:apps = @([pscustomobject]@{ name = 'Plain'; exe = $exe; source = 'github'; repo = 'someone/game-mod'; update = @('page') })
$rows = @(Get-AppRows -Force 6>$null)
Check 'plain app: old .exe still flagged UPDATE'       ($rows[0].Status -eq 'UPDATE')

"`n$fails failure(s)"
Remove-Item -Recurse -Force $root
