# The Scan feature: recognized apps from known-apps.json, itch.io pages, ready-to-add entries,
# tracked apps left out, and EmuDeck-only update methods dropped when EmuDeck isn't installed.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)
$script = Join-Path $repo 'Find-Sources.ps1'
$fails = 0
function Check($n, $c) { if ($c) { "PASS  $n" } else { "FAIL  $n"; $script:fails++ } }

# --- URL and entry helpers ------------------------------------------------------
$ast = [System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$null)
$forgeHosts = '(?i)(^|\.)(github\.com|githubusercontent\.com|gitlab\.com|codeberg\.org|bitbucket\.org|sourceforge\.net|gitea\.com)$|^git\.'
$ast.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.FunctionDefinitionAst] } | ForEach-Object { . ([scriptblock]::Create($_.Extent.Text)) }
$i = Get-RepoFromUrl 'https://cobalttornado1.itch.io/lightbound'
Check 'itch.io game page recognized' ($i -and $i.Site -eq 'itch.io' -and $i.Url -eq 'https://cobalttornado1.itch.io/lightbound')
$i = Get-RepoFromUrl 'https://dev.itch.io/game/download/abc123'
Check 'itch.io download page keeps the game page' ($i -and $i.Url -eq 'https://dev.itch.io/game')
Check 'itch.io front page is not a game' ($null -eq (Get-RepoFromUrl 'https://www.itch.io/games'))
$d = New-AppDef 'Tool' @{ Site = 'github.com'; Repo = 'o/tool' } 'C:\Apps\Tool\tool.exe'
Check 'GitHub entry' ($d.source -eq 'github' -and $d.repo -eq 'o/tool' -and $d.exe -eq 'C:\Apps\Tool\tool.exe' -and ($d.update -join ',') -eq 'download,page')
$d = New-AppDef 'Game' @{ Site = 'itch.io'; Url = 'https://dev.itch.io/game' } 'C:\Games\game.exe'
Check 'itch.io entry opens the page' ($d.source -eq 'itch' -and $d.url -eq 'https://dev.itch.io/game' -and ($d.update -join ',') -eq 'page')
$d = New-AppDef 'Lab' @{ Site = 'gitlab.com'; Repo = 'grp/sub/lab' } 'x.exe'
Check 'GitLab entry uses the API' ($d.source -eq 'gitlab' -and $d.api -eq 'https://gitlab.com/api/v4/projects/grp%2Fsub%2Flab/releases/permalink/latest')
$d = New-AppDef 'Berg' @{ Site = 'codeberg.org'; Repo = 'o/berg' } 'x.exe'
Check 'Codeberg entry uses the Forgejo API' ($d.source -eq 'gitea' -and $d.api -eq 'https://codeberg.org/api/v1/repos/o/berg/releases/latest')

# --- turning scan rows into the checklist --------------------------------------
$trackedRepos = @('o/tracked'); $trackedUrls = @()
$rows = New-Object System.Collections.Generic.List[object]
Add-Row 'Known' 'PCSX2' 'PCSX2/pcsx2 (GitHub)' 'PCSX2/pcsx2' $false 'C:\Emu\PCSX2\pcsx2.exe' '' '' ([pscustomobject]@{ name = 'PCSX2' })
Add-Row 'App' 'PCSX2' 'github.com' 'PCSX2/pcsx2' $false 'C:\Emu\PCSX2\pcsx2.exe' ''
Add-Row 'App' 'CoolTool' 'github.com' 'dev/cooltool' $false 'C:\Apps\CoolTool\cool.exe' ''
Add-Row 'App' 'Game' 'itch.io' '' $false 'C:\Games\Game\game.exe' '' 'https://dev.itch.io/game'
Add-Row 'App' 'Old' 'github.com' 'o/tracked' $false 'C:\Apps\Old\old.exe' ''
Add-Row 'App' 'Random' '' '' $false 'C:\Apps\Random\r.exe' ''
Add-Row 'Download' 'cooltool-2.0.zip' 'github.com' 'dev/cooltool' $false 'C:\Users\u\Downloads\cooltool-2.0.zip' ''
Add-Row 'Download' 'other-1.0.zip' 'github.com' 'dev/other' $false 'C:\Users\u\Downloads\other-1.0.zip' ''
Add-Row 'Download' 'banana-native-v0.2.4.apk' 'git.eden-emu.dev' 'moceure/BananaNative' $false 'C:\EmulationSync\banana-native-v0.2.4.apk' ''
Add-Row 'Download' 'tool-1.0-linux-x64.tar.gz' 'github.com' 'dev/linuxonly' $false 'C:\Users\u\Downloads\tool-1.0-linux-x64.tar.gz' ''
Add-Row 'Download' 'mod-1.0.zip' 'github.com' 'dev/hidden' $false 'C:\Users\u\Downloads\mod-1.0.zip' ''
$nwAst = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq '$notWindows' }, $false)
$notWindows = $nwAst.Right.Extent.Text.Trim().Trim("'")
$script:ScanIgnore = @('dev/hidden')
$cands = @(Get-ScanCandidates)
$byName = @{}; foreach ($x in $cands) { $byName[$x.Name] = $x }
Check 'Android download (.apk) left out' (-not $byName.ContainsKey('BananaNative'))
Check 'Linux-only download left out' (-not $byName.ContainsKey('linuxonly'))
Check '"Don''t show again" item left out and counted' (-not $byName.ContainsKey('hidden') -and $script:HiddenCount -eq 1)
Check 'candidates carry the key used to hide them' ($byName['other'].Key -eq 'dev/other')
Check 'recognized app listed once (not again as a found program)' (@($cands | Where-Object { $_.Name -eq 'PCSX2' }).Count -eq 1 -and $byName['PCSX2'].Kind -eq 'known')
Check 'program from GitHub listed and ticked' ($byName['CoolTool'].Kind -eq 'found' -and $byName['CoolTool'].Selected -and $byName['CoolTool'].Def.repo -eq 'dev/cooltool')
Check 'program from an itch.io page listed' ($byName['Game'].Def.source -eq 'itch' -and $byName['Game'].Def.url -eq 'https://dev.itch.io/game')
Check 'already tracked repo left out' (-not $byName.ContainsKey('Old'))
Check 'program of unknown origin left out' (-not $byName.ContainsKey('Random'))
Check 'download for an installed program not repeated' (@($cands | Where-Object { $_.From -like 'dev/cooltool*' }).Count -eq 1)
Check 'download with no program found needs Locate, unticked' ($byName['other'].Kind -eq 'download' -and -not $byName['other'].Selected -and -not $byName['other'].Exe)

# --- full scan in a fake profile ------------------------------------------------
$root = Join-Path ([IO.Path]::GetTempPath()) ("winscan-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$saved = @{ APPDATA = $env:APPDATA; LOCALAPPDATA = $env:LOCALAPPDATA; USERPROFILE = $env:USERPROFILE }
try {
    $env:APPDATA = Join-Path $root 'Roaming'; $env:LOCALAPPDATA = Join-Path $root 'Local'; $env:USERPROFILE = Join-Path $root 'User'
    $emu = Join-Path $root 'Emu'
    function New-File($p) { New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null; Set-Content $p 'x'; $p }
    [void](New-File (Join-Path $env:APPDATA 'EmuDeck/Emulators/PCSX2-Qt/pcsx2-qtx64.exe'))
    [void](New-File (Join-Path $env:APPDATA 'EmuDeck/Emulators/xemu/xemu.exe'))
    [void](New-File (Join-Path $env:LOCALAPPDATA 'Playnite/Playnite.dll'))
    [void](New-File (Join-Path $emu 'tools/srm.exe'))
    New-Item -ItemType Directory -Force (Join-Path $env:APPDATA 'EmuDeck') | Out-Null
    Set-Content (Join-Path $env:APPDATA 'EmuDeck/settings.ps1') "`$emulationPath=`"$emu`""

    # Only the fake profile is searched. The defaults include C:\Emulation and the real Desktop,
    # which on a real PC means walking a whole emulation library on every run.
    $roots = @{
        DownloadRoots = @((Join-Path $env:USERPROFILE 'Downloads'), $emu)
        RepoRoots     = @()
        AppRoots      = @((Join-Path $env:USERPROFILE 'Wintainium\Apps'), (Join-Path $emu 'tools'), (Join-Path $env:APPDATA 'EmuDeck\Emulators'))
        ModRoots      = @($emu)
    }
    $work = Join-Path $root 'Wintainium'; New-Item -ItemType Directory $work | Out-Null
    Copy-Item $script, (Join-Path $repo 'known-apps.json') $work
    # xemu is already tracked
    ConvertTo-Json -InputObject @(@{ name = 'xemu'; exe = '%APPDATA%\EmuDeck\Emulators\xemu\xemu.exe'; source = 'github'; repo = 'xemu-project/xemu'; update = @('page') }) |
        Set-Content (Join-Path $work 'wintainium-apps.json')

    $res = & (Join-Path $work 'Find-Sources.ps1') @roots -PassThru -SkipRepos 6>$null
    $c = @($res.Candidates)
    $names = @($c | ForEach-Object { $_.Name })
    Check 'recognized apps found (PCSX2, Playnite)' ($names -contains 'PCSX2' -and $names -contains 'Playnite')
    Check 'EmuDeck folder read from its settings (Steam ROM Manager)' ($names -contains 'Steam ROM Manager')
    Check 'tracked app (xemu) left out' ($names -notcontains 'xemu')
    $p = $c | Where-Object Name -eq 'PCSX2'
    Check 'recognized apps are ticked' ($p.Selected -eq $true -and $p.Kind -eq 'known')
    Check 'entry keeps the portable %APPDATA% path' ($p.Def.exe -eq '%APPDATA%\EmuDeck\Emulators\PCSX2-Qt\pcsx2-qtx64.exe')
    Check 'without EmuDeck installed, its update method is dropped' (($p.Def.update -join ',') -eq 'inapp,page' -and -not $p.Def.emudeckFn)
    $pl = $c | Where-Object Name -eq 'Playnite'
    Check 'catalog extras kept (launch, hint)' ($pl.Def.launch -and $pl.Def.hint -and $pl.Def.repo -eq 'JosefNemec/Playnite')

    [void](New-File (Join-Path $env:APPDATA 'EmuDeck/backend/functions/all.ps1'))
    $res = & (Join-Path $work 'Find-Sources.ps1') @roots -PassThru -SkipRepos 6>$null
    $p = @($res.Candidates) | Where-Object Name -eq 'PCSX2'
    Check 'with EmuDeck installed, updates go through EmuDeck' (($p.Def.update -join ',') -eq 'emudeck,inapp,page' -and $p.Def.emudeckFn -eq 'PCSX2QT_install')

    # Wintainium itself is offered once its own files are there
    Set-Content (Join-Path $work 'Wintainium.ps1') '# stub'
    $res = & (Join-Path $work 'Find-Sources.ps1') @roots -PassThru -SkipRepos 6>$null
    $w = @($res.Candidates) | Where-Object Name -eq 'Wintainium'
    Check 'Wintainium offered as a ticked entry' ($w -and $w.Selected -and $w.Kind -eq 'known' -and $w.Def.repo -eq 'Ay1man2/Wintainium' -and ($w.Def.update -join ',') -eq 'page')
    Check 'Wintainium entry points at its script when there is no .exe' ($w.Def.exe -eq (Join-Path $work 'Wintainium.ps1'))
    Set-Content (Join-Path $work 'Wintainium.exe') 'x'
    $res = & (Join-Path $work 'Find-Sources.ps1') @roots -PassThru -SkipRepos 6>$null
    $w = @($res.Candidates) | Where-Object Name -eq 'Wintainium'
    Check 'Wintainium entry prefers Wintainium.exe' ($w.Def.exe -eq (Join-Path $work 'Wintainium.exe'))

    # hidden entries stay hidden, and the scan says how many
    ConvertTo-Json @{ scanIgnore = @('pcsx2/pcsx2') } | Set-Content (Join-Path $work 'wintainium-settings.json')
    $res = & (Join-Path $work 'Find-Sources.ps1') @roots -PassThru -SkipRepos 6>$null
    Check 'hidden recognized app left out' (@($res.Candidates | Where-Object Name -eq 'PCSX2').Count -eq 0 -and $res.Hidden -eq 1)
    Remove-Item (Join-Path $work 'wintainium-settings.json')

    # once tracked, Wintainium isn't offered again
    $cfg = Join-Path $work 'wintainium-apps.json'
    $list = @(Get-Content -Raw $cfg | ConvertFrom-Json) + [pscustomobject]@{ name = 'Wintainium'; exe = (Join-Path $work 'Wintainium.exe'); source = 'github'; repo = 'Ay1man2/Wintainium'; update = @('page') }
    ConvertTo-Json -InputObject $list | Set-Content $cfg
    $res = & (Join-Path $work 'Find-Sources.ps1') @roots -PassThru -SkipRepos 6>$null
    Check 'tracked Wintainium not offered again' (@($res.Candidates | Where-Object Name -eq 'Wintainium').Count -eq 0)

    $csv = Join-Path $root 'out.csv'
    $out = & (Join-Path $work 'Find-Sources.ps1') @roots -SkipRepos -OutCsv $csv *>&1 | Out-String
    Check 'console report lists recognized apps' ($out -match 'Apps Wintainium recognizes' -and $out -match 'Click Scan in Wintainium')
    Check 'console report still writes the CSV' (Test-Path $csv)
} finally {
    foreach ($k in $saved.Keys) { Set-Item "env:$k" $saved[$k] }
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}
"`n$fails failure(s)"
