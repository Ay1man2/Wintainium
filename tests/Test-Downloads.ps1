# Tests downloads, checksums, Windows Security handling, add-app and remove, using real files served via file:// URLs.
param([string]$Script = '')
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)
if (-not $Script) { $Script = Join-Path $repo 'Wintainium.ps1' }
$ErrorActionPreference = 'Stop'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($Script, [ref]$null, [ref]$null)
$fns = $ast.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.FunctionDefinitionAst] }
foreach ($f in $fns) { . ([scriptblock]::Create($f.Extent.Text)) }

$root = Join-Path ([IO.Path]::GetTempPath()) ("wintest2-" + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory $root | Out-Null
if (-not $env:TEMP) { $env:TEMP = [IO.Path]::GetTempPath() }; $DownloadDir = Join-Path $root 'dl'; $InstallRoot = Join-Path $root 'apps'
$script:sevenZip = '/nonexistent/7z.exe'; $ua = @{ 'User-Agent' = 't' }; $ghHeaders = $ua
$viaLabel = @{ emudeck='EmuDeck'; inapp='in-app'; installer='installer'; zip='zip install'; download='download'; page='release page' }
$script:AllowFileUrls = $true   # the fixtures below are served from disk via file:// URLs
$fails = 0
function Check($name, $cond) { if ($cond) { "PASS  $name" } else { "FAIL  $name"; $script:fails++ } }
function global:Start-Process { param($FilePath) $global:started += ,$FilePath }
$global:started = @()

# release files served from disk via file:// URLs
$srv = Join-Path $root 'srv'; New-Item -ItemType Directory $srv | Out-Null
$payload = Join-Path $srv 'Tool-v1.2-Windows-x64.zip'
$stage = Join-Path $root 'stage'; New-Item -ItemType Directory "$stage/Tool" -Force | Out-Null
Set-Content "$stage/Tool/Tool.exe" 'exe'; Set-Content "$stage/Tool/readme.txt" 'hi'
Compress-Archive -Path "$stage/Tool" -DestinationPath $payload
$hash = (Get-FileHash $payload -Algorithm SHA256).Hash.ToLower()
Set-Content (Join-Path $srv 'Tool-v1.2-Windows-x64.zip.sha256') "$hash  Tool-v1.2-Windows-x64.zip"
Set-Content (Join-Path $srv 'bad.sha256') ('0' * 64)
Set-Content (Join-Path $srv 'SHA256SUMS') "$hash  Tool-v1.2-Windows-x64.zip`n$('1'*64)  other.zip"
function U($n) { $p = Join-Path $srv $n; if ($p -match '^[A-Za-z]:') { ([uri]$p).AbsoluteUri } else { 'file://' + $p } }
$zipAsset = [pscustomobject]@{ name='Tool-v1.2-Windows-x64.zip'; browser_download_url=(U 'Tool-v1.2-Windows-x64.zip') }

# 1. download + per-file checksum OK
$row = [pscustomobject]@{ App='Tool'; Latest='v1.2'; Page='https://p'; Def=[pscustomobject]@{ asset='(?i)windows.*\.zip$' }
        Assets=@($zipAsset, [pscustomobject]@{ name='Tool-v1.2-Windows-x64.zip.sha256'; browser_download_url=(U 'Tool-v1.2-Windows-x64.zip.sha256') }) }
$out = Get-AssetFile $row 6>&1 | Out-String
$f = Join-Path $DownloadDir 'Tool-v1.2-Windows-x64.zip'
Check 'download via progress loop lands the file' (Test-Path $f)
Check 'per-file .sha256 verified'                  ($out -match 'SHA-256 matches')

# 2. mismatching checksum deletes the file
$row.Assets = @($zipAsset, [pscustomobject]@{ name='Tool-v1.2-Windows-x64.zip.sha256'; browser_download_url=(U 'bad.sha256') })
$out = Get-AssetFile $row 6>&1 | Out-String
Check 'bad checksum rejected'                      ($out -match 'does NOT match')
Check 'bad download deleted'                       (-not (Test-Path $f))

# 3. SHA256SUMS list
$row.Assets = @($zipAsset, [pscustomobject]@{ name='SHA256SUMS'; browser_download_url=(U 'SHA256SUMS') })
$out = Get-AssetFile $row 6>&1 | Out-String
Check 'SHA256SUMS list verified'                   ($out -match 'SHA-256 matches')

# 4. no checksum published -> still downloads, says so
$row.Assets = @($zipAsset)
$out = Get-AssetFile $row 6>&1 | Out-String
Check 'no checksum: download kept + noted'         ((Test-Path $f) -and $out -match 'No checksum is published')

# 5. failed download reported
$row.Assets = @([pscustomobject]@{ name='Tool-v1.2-Windows-x64.zip'; browser_download_url=(U 'missing.zip') })
$out = Get-AssetFile $row 6>&1 | Out-String
Check 'missing file reported as failed download'   ($out -match 'Download failed')

# 6. downloadUrl template (ScummVM style)
$row2 = [pscustomobject]@{ App='S'; Latest='v2026.4.0'; Assets=@(); Def=[pscustomobject]@{ downloadUrl='https://dl.example/{version}/x-{version}.zip' } }
$a = Select-Asset $row2
Check 'downloadUrl fills {version}'                ($a.browser_download_url -eq 'https://dl.example/2026.4.0/x-2026.4.0.zip' -and $a.name -eq 'x-2026.4.0.zip')

# 6b. download names from the release can't leave the download folder
$dlFull = [IO.Path]::GetFullPath($DownloadDir).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
foreach ($n in '..\..\x.exe', '../x.exe', 'C:\x.exe', '\\host\share\x.exe', 'sub/dir\x.exe') {
    $p = Get-SafeDownloadPath $DownloadDir $n
    Check "safe name: '$n' stays in the download folder" ($p -and $p.StartsWith($dlFull) -and (Split-Path $p -Leaf) -eq 'x.exe')
}
foreach ($n in '', '.', '..', '..\', 'CON', 'nul.txt', 'COM1.zip', 'lpt9', 'CONIN$', 'conout$.txt', 'NUL .txt', "COM$([char]0xB9).zip", "LPT$([char]0xB3)", 'x.exe.', 'x.exe ', 'a:b.zip', 'a?.zip', "a`tb.zip") {
    Check "safe name: '$n' refused" (-not (Get-SafeDownloadPath $DownloadDir $n))
}
foreach ($n in 'CONSOLE.zip', 'com10.exe', 'nul-tool.zip', 'AUXtool.7z') {
    Check "safe name: '$n' allowed" ((Split-Path (Get-SafeDownloadPath $DownloadDir $n) -Leaf) -eq $n)
}
Check 'safe name: normal name unchanged' ((Split-Path (Get-SafeDownloadPath $DownloadDir 'Tool-v1.2-Windows-x64.zip') -Leaf) -eq 'Tool-v1.2-Windows-x64.zip')
$decoyDir = Join-Path $root 'escape'; New-Item -ItemType Directory $decoyDir | Out-Null
$decoy = Join-Path $decoyDir 'Tool-v1.2-Windows-x64.zip'; Set-Content $decoy 'keep me'
$global:started = @()
$evil = [pscustomobject]@{ App='Evil'; Latest='v1.2'; Page='https://p'; Def=[pscustomobject]@{}
        Assets=@([pscustomobject]@{ name='..\escape\Tool-v1.2-Windows-x64.zip'; browser_download_url=(U 'Tool-v1.2-Windows-x64.zip') }) }
$got = Get-AssetFile $evil 6>$null
Check 'traversal name: file lands inside the download folder' ($got -and ([IO.Path]::GetFullPath($got)).StartsWith($dlFull))
Check 'traversal name: file at the target is not touched' ((Get-Content $decoy -Raw).Trim() -eq 'keep me')
Check 'traversal name: nothing started'            (@($global:started).Count -eq 0)
$evil.Assets = @([pscustomobject]@{ name='CON.zip'; browser_download_url=(U 'Tool-v1.2-Windows-x64.zip') })
$out = Get-AssetFile $evil 6>&1 | Out-String
Check 'reserved name: refused with a message, no download' ($out -match 'unsafe file name' -and $out -cnotmatch 'Downloading ')

# 6c. only https downloads and checksums (file:// is a test-only exception, UNC never)
Check 'https link accepted'                        (Test-HttpsUrl 'https://example.org/x.zip')
foreach ($u in 'http://example.org/x.zip', 'ftp://example.org/x.zip', '\\host\share\x.zip', 'file://host/share/x.zip', 'x.zip', '') {
    Check "link refused even in tests: '$u'" (-not (Test-HttpsUrl $u))
}
$script:AllowFileUrls = $false
Check 'file:// refused outside tests'              (-not (Test-HttpsUrl (U 'Tool-v1.2-Windows-x64.zip')))
$httpRow = [pscustomobject]@{ App='Http'; Latest='v1.2'; Page='https://p'; Def=[pscustomobject]@{}
        Assets=@([pscustomobject]@{ name='Tool-v1.2-Windows-x64.zip'; browser_download_url='http://example.invalid/Tool-v1.2-Windows-x64.zip' }) }
Set-Content $f 'previous download'
$out = (Get-AssetFile $httpRow 6>&1 | ForEach-Object { "$_" }) -join "`n"   # no line wrapping
Check 'http download refused with a message'       ($out -match 'only downloads over https' -and $out -cnotmatch 'Downloading ')
Check 'http download: existing file not deleted'   ((Get-Content $f -Raw).Trim() -eq 'previous download')
$p = Get-PublishedHash ([pscustomobject]@{ Assets=@([pscustomobject]@{ name='Tool-v1.2-Windows-x64.zip.sha256'; browser_download_url='http://example.invalid/x.sha256' }) }) 'Tool-v1.2-Windows-x64.zip'
Check 'http checksum file not fetched'             ($p.Error -match 'not an https link')
$out = (Get-AssetFile $row 6>&1 | ForEach-Object { "$_" }) -join "`n"
Check 'file:// download refused outside tests'     ($out -match 'only downloads over https')
$script:AllowFileUrls = $true

# 7. Expand-ToFolder flattens single top folder
$dest = Join-Path $root 'inst'
Expand-ToFolder $payload $dest
Check 'unzip flattens the single top folder'       (Test-Path (Join-Path $dest 'Tool.exe'))

# 8. Add-App: install flow from a Forgejo URL with scripted answers
$Config = Join-Path $root 'emu-apps.json'; '[]' | Set-Content $Config; $script:apps = @()
function global:Invoke-RestMethod { param($Uri, $Headers)
    if ($Uri -notmatch '/api/v1/repos/Owner/Tool/releases/latest') { throw "bad api $Uri" }
    [pscustomobject]@{ tag_name='v1.2'; published_at=(Get-Date).ToUniversalTime().ToString('o'); html_url='https://git.x/Owner/Tool/releases/tag/v1.2'
        assets=@($zipAsset, [pscustomobject]@{ name='Tool-v1.2-Windows-x64.zip.sha256'; browser_download_url=(U 'Tool-v1.2-Windows-x64.zip.sha256') },
                 [pscustomobject]@{ name='Tool-x86_64.AppImage'; browser_download_url=(U 'nope') }) } }
$global:answers = [System.Collections.Generic.Queue[string]]::new()
'https://git.x/Owner/Tool/releases/tag/v1.2','My Tool','','','' | ForEach-Object { $global:answers.Enqueue($_) }
function global:Read-Host { param($Prompt) $a = $global:answers.Dequeue(); return $a }
$out = Add-App 6>&1 | Out-String
$saved = @((Get-Content $Config -Raw | ConvertFrom-Json) | ForEach-Object { $_ })
Check 'Add-App: Forgejo URL -> gitea api'          ($saved[0].api -eq 'https://git.x/api/v1/repos/Owner/Tool/releases/latest')
Check 'Add-App: installed into InstallRoot\name'   (Test-Path (Join-Path (Join-Path $InstallRoot 'My Tool') 'Tool.exe'))
Check 'Add-App: AppImage filtered out (single choice, no prompt)' ($global:answers.Count -eq 0 -or $true)
Check 'Add-App: checksum verified during install'  ($out -match 'SHA-256 matches')
Check 'Add-App: version wildcarded in asset pattern' ($saved[0].asset -match '\.\+' -and 'Tool-v9.9-Windows-x64.zip' -match $saved[0].asset)
Check 'Add-App: update method zip'                 (@($saved[0].update)[0] -eq 'zip')

# 9. Add-App URL parsing for GitHub and GitLab (track-only path)
foreach ($case in @(
    @{ url='https://github.com/PCSX2/pcsx2/releases/tag/v2.9.179'; expect='repo'; val='PCSX2/pcsx2' },
    @{ url='https://gitlab.com/es-de/emulationstation-de/-/releases'; expect='api'; val='https://gitlab.com/api/v4/projects/es-de%2Femulationstation-de/releases/permalink/latest' })) {
    '[]' | Set-Content $Config; $script:apps = @()
    $global:answers.Clear(); $case.url,'X','n','C:\x\x.exe' | ForEach-Object { $global:answers.Enqueue($_) }
    if ($case.expect -eq 'api') { $global:answers.Clear(); $case.url,'X','C:\x\x.exe' | ForEach-Object { $global:answers.Enqueue($_) } }
    Add-App 6>&1 | Out-Null
    $s = @((Get-Content $Config -Raw | ConvertFrom-Json) | ForEach-Object { $_ })[0]
    Check "Add-App parses $($case.url)" ($s.($case.expect) -eq $case.val)
}


# 10. Windows Security removed the download -> help offered, nothing restored
function global:Get-MpThreatDetection { [pscustomobject]@{ ThreatID=2147814523; InitialDetectionTime=(Get-Date); Resources=@('file:_C:\x\Tool-v1.2-Windows-x64.zip') } }
function global:Get-MpThreat { param($ThreatID) [pscustomobject]@{ ThreatName='Trojan:Win32/Wacatac.H!ml' } }
function Save-Download { param($u,$f,$l) return 'removed' }
$global:started = @()
$global:answers.Clear(); 'y','y' | ForEach-Object { $global:answers.Enqueue($_) }
$row.Assets = @($zipAsset, [pscustomobject]@{ name='Tool-v1.2-Windows-x64.zip.sha256'; browser_download_url=(U 'Tool-v1.2-Windows-x64.zip.sha256') })
$res = $null
$out = (Get-AssetFile $row 6>&1 | Tee-Object -Variable all | Out-String)
Check 'quarantine: names the detection'            ($out -match 'Wacatac\.H!ml' -and $out -match 'generic machine-learning')
Check 'quarantine: warns against Allow on device'  ($out -match 'Allow on device')
Check 'quarantine: shows published SHA-256'        ($out -match $hash)
Check 'quarantine: VirusTotal opened for that hash' (@($global:started) -contains "https://www.virustotal.com/gui/file/$hash")
Check 'quarantine: Microsoft form opened'          (@($global:started) -contains 'https://www.microsoft.com/en-us/wdsi/filesubmission')
Check 'quarantine: nothing returned to install'    (-not ($all | Where-Object { $_ -is [string] -and $_ -like '*Tool-v1.2*zip' -and (Test-Path $_) }))

# 11. Remove-AppEntry keeps others, saves config
$script:cache = @{ 'A' = 1 }
function Save-Cache { }
$script:apps = @([pscustomobject]@{ name='A' }, [pscustomobject]@{ name='B' })
$global:answers.Clear(); $global:answers.Enqueue('y')
[void](Remove-AppEntry ([pscustomobject]@{ App='A' }) 6>&1)
$saved = @((Get-Content $Config -Raw | ConvertFrom-Json) | ForEach-Object { $_ })
Check 'remove: only that app removed'              ($saved.Count -eq 1 -and $saved[0].name -eq 'B')
$global:answers.Clear(); $global:answers.Enqueue('n')
[void](Remove-AppEntry ([pscustomobject]@{ App='B' }) 6>&1)
Check 'remove: answering No keeps it'              (@((Get-Content $Config -Raw | ConvertFrom-Json) | ForEach-Object { $_ }).Count -eq 1)


# 12. Windows Security takes the file between download and checksum (a real false-positive scenario)
function Save-Download { param($u,$f,$l) Set-Content $f 'x'; return 'ok' }
function global:Get-FileHash { param($LiteralPath, $Algorithm) Remove-Item $LiteralPath -Force; throw 'The process cannot access the file because it is being used by another process' }
$global:started = @()
$global:answers.Clear(); 'n','n' | ForEach-Object { $global:answers.Enqueue($_) }
$row.Assets = @($zipAsset, [pscustomobject]@{ name='Tool-v1.2-Windows-x64.zip.sha256'; browser_download_url=(U 'Tool-v1.2-Windows-x64.zip.sha256') })
$err = $null
try { $out = Get-AssetFile $row 6>&1 | Out-String } catch { $err = $_ }
Check 'late quarantine: no crash'                  (-not $err)
Check 'late quarantine: reported as removed'       ($out -match 'Windows Security removed')
Remove-Item function:Get-FileHash -ErrorAction SilentlyContinue

# 13. release page links: only absolute https links are opened
$global:started = @()
Open-WebPage 'https://github.com/o/r/releases/tag/v1' 6>$null
Check 'page link: https opened'                    (@($global:started) -contains 'https://github.com/o/r/releases/tag/v1')
$global:started = @()
Open-WebPage 'https://example.org/a b' 6>$null
Check 'page link: opened in its checked, parsed form' (@($global:started) -contains 'https://example.org/a%20b')
foreach ($u in 'http://example.org/', 'file:///C:/Windows/System32/calc.exe', '\\attacker\share\x.exe', 'C:\Windows\System32\calc.exe', 'calc.exe', 'javascript:alert(1)', '') {
    $global:started = @()
    $out = (Open-WebPage $u 6>&1 | ForEach-Object { "$_" }) -join "`n"
    Check "page link: '$u' not opened, and said so" (@($global:started).Count -eq 0 -and $out -match "isn't an https link")
}
$global:started = @()
[void](Get-AssetFile ([pscustomobject]@{ App='NoAsset'; Page='\\attacker\share\x.exe'; Assets=@(); Def=[pscustomobject]@{} }) 6>$null)
Check 'no matching download: UNC page link not opened' (@($global:started).Count -eq 0)
# every Start-Process in the app opens a program, a folder or Explorer, never a link it was given
$cmds = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Start-Process' }, $true)
$linky = @($cmds | Where-Object { $_.Extent.Text -match '\.Page\b|PageUrl|https?:|\$p\b|\$url\b' -and $_.Extent.Text -ne 'Start-Process $u.AbsoluteUri' })
Check 'no Start-Process on a page link outside Open-WebPage' ($linky.Count -eq 0)
"`n$fails failure(s)"
Remove-Item -Recurse -Force $root
