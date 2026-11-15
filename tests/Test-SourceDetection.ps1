$ErrorActionPreference = 'Stop'
$path = (Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)) 'Find-Sources.ps1')
$ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$null)
$forgeHosts = '(?i)(^|\.)(github\.com|githubusercontent\.com|gitlab\.com|codeberg\.org|bitbucket\.org|sourceforge\.net|gitea\.com)$|^git\.'
$ast.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.FunctionDefinitionAst] } | ForEach-Object { . ([scriptblock]::Create($_.Extent.Text)) }
$fails = 0
$repoIdCache = @{}
function Resolve-GitHubRepoId($id) { if ($id -eq '1449838890') { 'someone/some-tool' } else { '' } }
function Check($n, $c) { if ($c) { "PASS  $n" } else { "FAIL  $n"; $script:fails++ } }
function RR($u) { $r = Get-RepoFromUrl $u; if ($r) { "$($r.Site)|$($r.Repo)" } else { 'null' } }
Check 'github release download'      ((RR 'https://github.com/PCSX2/pcsx2/releases/download/v2.9.179/x.7z') -eq 'github.com|PCSX2/pcsx2')
Check 'github releases page referrer' ((RR 'https://github.com/owner/project/releases') -eq 'github.com|owner/project')
Check 'release-assets host: site only' ((RR 'https://release-assets.githubusercontent.com/some/other/path?sp=r') -eq 'github.com|')
Check 'raw.githubusercontent repo'    ((RR 'https://raw.githubusercontent.com/o/r/main/f.txt') -eq 'github.com|o/r')
Check 'gitlab package file'           ((RR 'https://gitlab.com/es-de/emulationstation-de/-/package_files/357717909/download') -eq 'gitlab.com|es-de/emulationstation-de')
Check 'forgejo self-hosted'           ((RR 'https://git.example.org/team/tool/releases/download/v1.0/tool.exe') -eq 'git.example.org|team/tool')
Check 'codeberg'                      ((RR 'https://codeberg.org/owner/app/releases/download/1.0/a.zip') -eq 'codeberg.org|owner/app')
Check 'unknown host with /releases/download/' ((RR 'https://example.org/team/tool/releases/download/1/x.zip') -eq 'example.org|team/tool')
Check 'scummvm site is not a code host' ((RR 'https://downloads.scummvm.org/frs/scummvm/2026.4.0/x.zip') -eq 'null')
Check 'gamebanana is not a code host'  ((RR 'https://gamebanana.com/dl/123456') -eq 'null')
Check 'local path ignored'            ((RR 'C:\Users\a\Downloads\mod.zip') -eq 'null')
$u = 'git@github.com:Owner/Repo.git' -replace '^git@([^:]+):', 'https://$1/' -replace '\.git$', ''
Check 'ssh remote converted'          ((RR $u) -eq 'github.com|Owner/Repo')
Check 'release-assets repo id resolved' ((RR 'https://release-assets.githubusercontent.com/github-production-release-asset/1449838890/aef75aea?sp=r') -eq 'github.com|someone/some-tool')
Check 'old objects host repo id resolved' ((RR 'https://objects.githubusercontent.com/github-production-release-asset-2e65be/1449838890/abc') -eq 'github.com|someone/some-tool')
"`n$fails failure(s)"
