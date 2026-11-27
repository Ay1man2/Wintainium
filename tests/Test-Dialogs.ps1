# The window's question boxes: closing a Yes/No box with X (or Alt+F4) answers No, never Yes.
# Show-Prompt is loaded on its own from the script, with a stand-in for the WPF dialog that the
# user closes without clicking anything.
param([string]$Script = '')
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)
if (-not $Script) { $Script = Join-Path $repo 'Wintainium.ps1' }
$ErrorActionPreference = 'Stop'
$fails = 0
function Check($name, $cond) { if ($cond) { "PASS  $name" } else { "FAIL  $name"; $script:fails++ } }

$ast = [System.Management.Automation.Language.Parser]::ParseFile($Script, [ref]$null, [ref]$null)
$fn = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Show-Prompt' }, $true) | Select-Object -First 1
Check 'Show-Prompt found in the script' ($null -ne $fn)
. ([scriptblock]::Create($fn.Extent.Text))

# A stand-in for one WPF element: settable properties, and event hookups that do nothing.
function New-Element {
    $e = [pscustomobject]@{ Text = ''; Visibility = 'Visible'; Content = ''; IsCancel = $false; IsDefault = $false; Owner = $null; Icon = $null }
    $e | Add-Member ScriptMethod Add_Click { param($handler) }
    $e | Add-Member ScriptMethod Add_ContentRendered { param($handler) }
    $e | Add-Member ScriptMethod Focus { $true }
    $e | Add-Member ScriptMethod Close { }
    return $e
}
# The dialog: FindName hands out the elements, ShowDialog returns as if the user closed it with X.
function New-Xaml($xaml) {
    $global:els = @{}
    foreach ($n in 'Ctx', 'Prompt', 'Input', 'B1', 'B2', 'B3') { $global:els[$n] = New-Element }
    $dlg = New-Element
    $dlg | Add-Member ScriptMethod FindName { param($n) $global:els[$n] }
    $dlg | Add-Member ScriptMethod ShowDialog { $null }
    return $dlg
}
$script:promptContext = New-Object System.Collections.Generic.List[string]
$script:win = New-Element
$dialogXaml = ''

Check 'Yes/No box closed with X answers No'           ((Show-Prompt '  Install it now? (Y/n)') -eq 'n')
Check 'yes-default question closed with X answers No' ((Show-Prompt '  Run the installer now? (y/N)') -eq 'n')
Check 'Stop question closed with X answers No (skip)' ((Show-Prompt '  Update this one? (Y/n, q = stop)') -eq 'n')
Check 'Yes/No box shows Yes and No buttons'           ($global:els['B1'].Content -eq 'Yes' -and $global:els['B2'].Content -eq 'No')
Check 'text box closed with X answers nothing'        ((Show-Prompt '  App name') -eq '')

"`n$fails failure(s)"
