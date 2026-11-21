# The one-file Wintainium.exe: installs itself, updates without touching your files, never
# downgrades, leaves zip folders and git clones alone, and never runs a Wintainium.ps1 that just
# happens to sit next to it (e.g. in Downloads). Compiled with the C# compiler that
# ships with Windows (or Mono's mcs elsewhere); skipped when neither is available.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)
$fails = 0
function Check($n, $c) { if ($c) { "PASS  $n" } else { "FAIL  $n"; $script:fails++ } }

$onWindows = [Environment]::OSVersion.Platform -eq 'Win32NT'
$csc = $null; $mono = $null
if ($onWindows) {
    foreach ($fw in 'Framework64', 'Framework') {
        $c = Join-Path $env:WINDIR "Microsoft.NET\$fw\v4.0.30319\csc.exe"
        if (-not $csc -and (Test-Path -LiteralPath $c)) { $csc = $c }
    }
} else {
    $c = Get-Command mcs -ErrorAction SilentlyContinue
    $m = Get-Command mono -ErrorAction SilentlyContinue
    if ($c -and $m) { $csc = $c.Source; $mono = $m.Source }
}
if (-not $csc) { 'SKIP  no C# compiler found'; "`n0 failure(s)"; return }

$root = Join-Path ([IO.Path]::GetTempPath()) ("winexe-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $root | Out-Null
$savedDir = $env:WINTAINIUM_INSTALL_DIR
try {
    # payload: stand-in scripts with the real file names
    $src = Join-Path $root 'src'; New-Item -ItemType Directory -Path (Join-Path $src 'assets') -Force | Out-Null
    foreach ($f in 'Wintainium.ps1', 'Find-Sources.ps1', 'known-apps.json') { Set-Content (Join-Path $src $f) "v1 $f" }
    Copy-Item (Join-Path $repo 'assets/icon.ico') (Join-Path $src 'assets/icon.ico')

    function Build([string]$ver, [string]$out, [switch]$NoPayload) {
        $cs = (Get-Content (Join-Path $repo 'launcher/WintainiumLauncher.cs') -Raw) -replace '"0\.0\.0\.0"', "`"$ver`""
        $tmp = Join-Path $root "L$ver.cs"; Set-Content -LiteralPath $tmp -Value $cs
        $res = @()
        if (-not $NoPayload) {
            foreach ($f in 'Wintainium.ps1', 'Find-Sources.ps1', 'known-apps.json', 'assets/icon.ico') {
                $res += "-resource:$(Join-Path $src $f),payload/$f"
            }
        }
        if ($onWindows) { $res = $res -replace '^-resource:', '/resource:'; & $csc /nologo /target:winexe /out:$out /reference:System.Windows.Forms.dll @res $tmp | Out-Null }
        else { & $csc -langversion:5 -target:winexe "-out:$out" -r:System.Windows.Forms.dll @res $tmp | Out-Null }
        if ($LASTEXITCODE) { throw "compile failed for $ver" }
    }
    function Invoke-Exe([string]$exe) {
        $p = if ($mono) { Start-Process $mono -ArgumentList "`"$exe`" --install-only" -Wait -PassThru -NoNewWindow }
             else { Start-Process $exe -ArgumentList '--install-only' -Wait -PassThru }
        return $p.ExitCode
    }

    $dl = Join-Path $root 'Downloads'; New-Item -ItemType Directory $dl | Out-Null
    $v1 = Join-Path $dl 'Wintainium.exe'; Build '0.2.0.0' $v1
    $inst = Join-Path $root 'Programs/Wintainium'
    $env:WINTAINIUM_INSTALL_DIR = $inst

    # 1. fresh install from a lone download
    $code = Invoke-Exe $v1
    Check 'installs from a lone download' ($code -eq 0 -and (Test-Path (Join-Path $inst 'Wintainium.ps1')) -and (Test-Path (Join-Path $inst 'Wintainium.exe')))
    Check 'installs the scripts and the icon' ((Test-Path (Join-Path $inst 'Find-Sources.ps1')) -and (Test-Path (Join-Path $inst 'known-apps.json')) -and (Test-Path (Join-Path $inst 'assets/icon.ico')))
    Check 'records the installed version' ((Get-Content (Join-Path $inst 'installed-version.txt') -Raw).Trim() -eq '0.2.0.0')

    # 2. a newer download updates the scripts and keeps your files
    Set-Content (Join-Path $inst 'wintainium-apps.json') '[ "mine" ]'
    Set-Content (Join-Path $inst 'wintainium-settings.json') '{ "mine": true }'
    foreach ($f in 'Wintainium.ps1', 'Find-Sources.ps1', 'known-apps.json') { Set-Content (Join-Path $src $f) "v2 $f" }
    $v2 = Join-Path $dl 'Wintainium-new.exe'; Build '0.2.1.0' $v2
    $code = Invoke-Exe $v2
    Check 'newer download updates the installed copy' ($code -eq 0 -and (Get-Content (Join-Path $inst 'Wintainium.ps1') -Raw) -match '^v2' -and (Get-Content (Join-Path $inst 'installed-version.txt') -Raw).Trim() -eq '0.2.1.0')
    Check 'update keeps your app list and settings' ((Get-Content (Join-Path $inst 'wintainium-apps.json') -Raw) -match 'mine' -and (Get-Content (Join-Path $inst 'wintainium-settings.json') -Raw) -match 'mine')
    Check 'installed exe is the new one' ((Get-Item (Join-Path $inst 'Wintainium.exe')).Length -eq (Get-Item $v2).Length)

    # 3. an older download never downgrades
    $code = Invoke-Exe $v1
    Check 'older download does not downgrade' ($code -eq 0 -and (Get-Content (Join-Path $inst 'installed-version.txt') -Raw).Trim() -eq '0.2.1.0' -and (Get-Content (Join-Path $inst 'Wintainium.ps1') -Raw) -match '^v2')

    # 4. Which nearby Wintainium.ps1 it trusts. With --install-only, a trusted folder means "run the
    #    scripts there" (so nothing is installed); an untrusted one means "install my own copy". Each
    #    case points the install folder at a fresh path to see which one happened.
    function Test-Folder([string]$name, [scriptblock]$fill) {
        $d = Join-Path $root $name; New-Item -ItemType Directory $d | Out-Null
        & $fill $d
        Copy-Item $v2 (Join-Path $d 'Wintainium.exe')
        $probe = Join-Path $root "probe-$name"; $env:WINTAINIUM_INSTALL_DIR = $probe
        $code = Invoke-Exe (Join-Path $d 'Wintainium.exe')
        return [pscustomobject]@{ Dir = $d; Code = $code; Installed = (Test-Path (Join-Path $probe 'Wintainium.ps1')) }
    }
    # 4a. a git clone with your own edits: runs in place, writes nothing
    $r = Test-Folder 'clone' { param($d) New-Item -ItemType Directory (Join-Path $d '.git') | Out-Null; Set-Content (Join-Path $d 'Wintainium.ps1') 'my working copy' }
    Check 'git clone: runs its own scripts, left untouched' ($r.Code -eq 0 -and -not $r.Installed -and (Get-Content (Join-Path $r.Dir 'Wintainium.ps1') -Raw).Trim() -eq 'my working copy' -and -not (Test-Path (Join-Path $r.Dir 'installed-version.txt')))
    # 4b. an untouched portable zip (same files as inside the exe): runs in place
    $r = Test-Folder 'portable' { param($d) Copy-Item (Join-Path $src '*') $d -Recurse }
    Check 'untouched portable zip: runs in place' ($r.Code -eq 0 -and -not $r.Installed)
    # 4c. a Wintainium.ps1 dropped into Downloads: not run; the exe installs its own copy
    $r = Test-Folder 'planted' { param($d) Set-Content (Join-Path $d 'Wintainium.ps1') 'planted' }
    Check 'planted script ignored, own copy installed' ($r.Code -eq 0 -and $r.Installed -and (Get-Content (Join-Path $r.Dir 'Wintainium.ps1') -Raw).Trim() -eq 'planted')
    # 4d. ... even with a planted installed-version.txt beside it
    $r = Test-Folder 'planted-marker' { param($d) Set-Content (Join-Path $d 'Wintainium.ps1') 'planted'; Set-Content (Join-Path $d 'installed-version.txt') '99.0.0.0' }
    Check 'planted script with a planted marker ignored' ($r.Code -eq 0 -and $r.Installed -and (Get-Content (Join-Path $r.Dir 'Wintainium.ps1') -Raw).Trim() -eq 'planted')
    # 4e. a genuine Wintainium.ps1 next to a changed Find-Sources.ps1: not trusted either
    $r = Test-Folder 'mixed' { param($d) Copy-Item (Join-Path $src '*') $d -Recurse; Set-Content (Join-Path $d 'Find-Sources.ps1') 'planted' }
    Check 'genuine script beside a changed helper not trusted' ($r.Code -eq 0 -and $r.Installed)
    $env:WINTAINIUM_INSTALL_DIR = $inst

    # 5. the installed copy refreshes its own scripts when its exe is newer than them
    Set-Content (Join-Path $inst 'installed-version.txt') '0.1.0.0'
    Set-Content (Join-Path $inst 'Wintainium.ps1') 'stale'
    $code = Invoke-Exe (Join-Path $inst 'Wintainium.exe')
    Check 'installed copy refreshes stale scripts' ($code -eq 0 -and (Get-Content (Join-Path $inst 'Wintainium.ps1') -Raw) -match '^v2')

    # 6. the plain launcher (no scripts inside) still needs the zip's files
    $bare = Join-Path $root 'bare'; New-Item -ItemType Directory $bare | Out-Null
    Build '0.2.0.0' (Join-Path $bare 'Wintainium.exe') -NoPayload
    $code = Invoke-Exe (Join-Path $bare 'Wintainium.exe')
    Check 'launcher without scripts reports an error' ($code -eq 1)
} finally {
    $env:WINTAINIUM_INSTALL_DIR = $savedDir
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}
"`n$fails failure(s)"
