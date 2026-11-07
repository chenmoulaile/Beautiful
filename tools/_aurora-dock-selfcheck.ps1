# Aurora Dock lane self-check (read-only): XAML well-formedness + DynamicResource key existence.
# ASCII output only. Run: powershell -NoProfile -File tools/_aurora-dock-selfcheck.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$xamls = @(
    'src/Beautiful.App/Dock/DockWindow.xaml',
    'src/Beautiful.App/Themes/MacOS.xaml',
    'src/Beautiful.App/Themes/Aurora.xaml',
    'src/Beautiful.App/Settings/SettingsWindow.xaml'
)

Write-Output '== [xml] well-formedness =='
foreach ($rel in $xamls) {
    $path = Join-Path $root $rel
    try {
        [xml]$doc = Get-Content -LiteralPath $path -Raw -Encoding UTF8
        Write-Output ("OK   {0}" -f $rel)
    } catch {
        Write-Output ("FAIL {0} : {1}" -f $rel, $_.Exception.Message)
    }
}

Write-Output ''
Write-Output '== DynamicResource keys used by DockWindow.xaml =='
[xml]$dock = Get-Content -LiteralPath (Join-Path $root 'src/Beautiful.App/Dock/DockWindow.xaml') -Raw -Encoding UTF8
$keys = New-Object System.Collections.Generic.HashSet[string]
function Walk($node) {
    if ($null -eq $node) { return }
    foreach ($attr in $node.Attributes) {
        if ($attr.Name -eq 'Background' -or $attr.Name -eq 'Fill' -or $attr.Name -eq 'Style' -or
            $attr.Name -eq 'BorderBrush' -or $attr.Name -eq 'Foreground' -or $attr.Name -eq 'CornerRadius' -or
            $attr.Name -eq 'Width' -or $attr.Name -eq 'Height' -or $attr.Name -eq 'Padding' -or
            $attr.Name -eq 'Margin' -or $attr.Name -eq 'Effect' -or $attr.Name -eq 'Clip' -or
            $attr.Name -eq 'Text' -or $attr.Name -eq 'Source' -or $attr.Name -eq 'FontFamily') {
            if ($attr.Value -like '{DynamicResource *}') {
                $k = $attr.Value.TrimStart('{DynamicResource ').TrimEnd('}').Trim()
                [void]$keys.Add($k)
            }
        }
        if ($attr.Name -eq 'Value' -and $attr.Value -like '{DynamicResource *}' -and $node.Name -like 'Setter') {
            $k = $attr.Value.TrimStart('{DynamicResource ').TrimEnd('}').Trim()
            [void]$keys.Add($k)
        }
    }
    if ($node.HasChildNodes) { foreach ($c in $node.ChildNodes) { Walk $c } }
}
Walk $dock.DocumentElement
Write-Output ($keys | Sort-Object | ForEach-Object { "KEY  $_" })

Write-Output ''
Write-Output '== key existence across dictionaries / runtime writers =='
$macos = Get-Content -LiteralPath (Join-Path $root 'src/Beautiful.App/Themes/MacOS.xaml') -Raw -Encoding UTF8
$aurora = Get-Content -LiteralPath (Join-Path $root 'src/Beautiful.App/Themes/Aurora.xaml') -Raw -Encoding UTF8
$runtime = ''
foreach ($f in @(
        'src/Beautiful.App/Themes/MacTokenDictionary.cs',
        'src/Beautiful.App/Themes/AuroraTokenDictionary.cs',
        'src/Beautiful.App/Themes/ThemeManager.cs',
        'src/Beautiful.App/Dock/DockWindow.xaml.cs')) {
    $runtime += Get-Content -LiteralPath (Join-Path $root $f) -Raw -Encoding UTF8
}
$missing = @()
foreach ($k in ($keys | Sort-Object)) {
    $inDock = $dock.DocumentElement.OuterXml -like "*x:Key=`"$k`"*"
    $inMacos = $macos -like "*x:Key=`"$k`"*"
    $inAurora = $aurora -like "*x:Key=`"$k`"*"
    $inRuntime = $runtime.Contains('"' + $k + '"')
    $status = if ($inMacos -and $inAurora) { 'BOTH' }
              elseif ($inDock) { 'WINDOW' }
              elseif ($inRuntime) { 'RUNTIME' }
              else { 'MISSING' }
    Write-Output ("{0,-8} {1}  macos={2} aurora={3} window={4} runtime={5}" -f $status, $k, $inMacos, $inAurora, $inDock, $inRuntime)
    if ($status -eq 'MISSING') { $missing += $k }
}
Write-Output ''
if ($missing.Count -eq 0) { Write-Output 'RESULT: no missing DynamicResource keys' } else { Write-Output ("RESULT: MISSING {0}" -f ($missing -join ', ')) }

Write-Output ''
Write-Output '== x:Name / event handler cross-check =='
$code = Get-Content -LiteralPath (Join-Path $root 'src/Beautiful.App/Dock/DockWindow.xaml.cs') -Raw -Encoding UTF8
$settingsCode = Get-Content -LiteralPath (Join-Path $root 'src/Beautiful.App/Settings/SettingsWindow.xaml.cs') -Raw -Encoding UTF8
foreach ($pair in @(
        @('PanelEnterShift', $dock.DocumentElement.OuterXml, $code),
        @('DockLaunchpadCheck', (Get-Content -LiteralPath (Join-Path $root 'src/Beautiful.App/Settings/SettingsWindow.xaml') -Raw -Encoding UTF8), $settingsCode),
        @('DockIndicatorCombo', (Get-Content -LiteralPath (Join-Path $root 'src/Beautiful.App/Settings/SettingsWindow.xaml') -Raw -Encoding UTF8), $settingsCode))) {
    $name = $pair[0]; $inXaml = $pair[1].Contains('x:Name="' + $name + '"'); $inCode = $pair[2].Contains($name)
    Write-Output ("{0}: xaml={1} code={2}" -f $name, $inXaml, $inCode)
}
foreach ($h in @('DockLaunchpad_Changed', 'DockIndicator_Changed')) {
    $inXaml = (Get-Content -LiteralPath (Join-Path $root 'src/Beautiful.App/Settings/SettingsWindow.xaml') -Raw -Encoding UTF8).Contains($h)
    $inCode = $settingsCode.Contains('private void ' + $h)
    Write-Output ("{0}: xaml-ref={1} code-handler={2}" -f $h, $inXaml, $inCode)
}
