# Static cross-check for ControlCenterPanel.xaml / .cs (no build, no compile).
# ASCII only on purpose: PowerShell 5.1 reads BOM-less files as ANSI.
$ErrorActionPreference = 'Stop'

$xamlPath = 'D:/Beautiful/src/Beautiful.App/Controls/ControlCenterPanel.xaml'
$csPath   = 'D:/Beautiful/src/Beautiful.App/Controls/ControlCenterPanel.xaml.cs'

[xml]$doc = Get-Content -Raw -Encoding UTF8 $xamlPath
Write-Output ('XML-WELLFORMED: OK root=' + $doc.DocumentElement.Name)

$cs = Get-Content -Raw -Encoding UTF8 $csPath

# --- collect resource keys defined under UserControl.Resources ---
$defined = New-Object System.Collections.Generic.HashSet[string]
function Walk-Keys($node) {
    foreach ($child in $node.ChildNodes) {
        if ($child.NodeType -eq 'Element') {
            $k = $child.GetAttribute('Key')
            if ($k) { [void]$defined.Add($k) }
            if ($child.Name -eq 'Style' -or $child.Name -eq 'LinearGradientBrush' -or $child.Name -eq 'DropShadowEffect') {
                $k2 = $child.GetAttribute('x:Key')
                if ($k2) { [void]$defined.Add($k2) }
            }
            Walk-Keys $child
        }
    }
}
Walk-Keys $doc.DocumentElement

# --- collect resource references: StaticResource / DynamicResource / FindResource ---
$referenced = New-Object System.Collections.Generic.HashSet[string]
foreach ($attrName in @('Style')) {
    foreach ($el in $doc.SelectNodes('//*[@Style]')) {
        $v = $el.GetAttribute('Style')
        if ($v -match 'StaticResource\s+([\w]+)') { [void]$referenced.Add($Matches[1]) }
    }
}
# any attribute containing {StaticResource X} / {DynamicResource X}
foreach ($el in $doc.SelectNodes('//*')) {
    foreach ($a in $el.Attributes) {
        $v = $a.Value
        if ($v -match '\{(?:Static|Dynamic)Resource\s+([\w]+)\}') { [void]$referenced.Add($Matches[1]) }
        if ($v -match '\{(?:Static|Dynamic)Resource\s+([\w]+)\}') {}
    }
}
# multi-ref attributes - global regex scan on comment-stripped raw text
$raw = Get-Content -Raw -Encoding UTF8 $xamlPath
$rawNoComments = [regex]::Replace($raw, '(?s)<!--.*?-->', '')
foreach ($m in [regex]::Matches($rawNoComments, '\{(?:Static|Dynamic)Resource\s+([\w]+)')) { [void]$referenced.Add($m.Groups[1].Value) }
foreach ($m in [regex]::Matches($cs, 'FindResource\("([\w]+)"\)')) { [void]$referenced.Add($m.Groups[1].Value) }
foreach ($m in [regex]::Matches($cs, 'Resources\["([\w]+)"\]')) { [void]$referenced.Add($m.Groups[1].Value) }

# keys that live outside this file (app-level theme dictionaries) - verified separately by grep
$appLevel = @('ThemeFontFamily','ThemeTextPrimary','ThemeAccent','MacControlFillHover','ThemeBackdropTint','ThemePanelBackground')

Write-Output ''
Write-Output ('DEFINED-KEYS (' + $defined.Count + '): ' + (($defined | Sort-Object) -join ', '))
Write-Output ''
$missing = @()
foreach ($r in $referenced) {
    if (-not $defined.Contains($r) -and -not ($appLevel -contains $r)) { $missing += $r }
}
Write-Output ('MISSING-IN-FILE (excluding app-level): ' + $(if ($missing.Count) { $missing -join ', ' } else { 'NONE' }))

# --- x:Name in XAML (regex on comment-stripped raw text; x: prefix needs a ns manager) ---
$names = @()
foreach ($m in [regex]::Matches($rawNoComments, 'x:Name="([\w]+)"')) {
    if ($names -notcontains $m.Groups[1].Value) { $names += $m.Groups[1].Value }
}
Write-Output ''
Write-Output ('XAML-NAMES (' + $names.Count + '): ' + ($names -join ', '))

# names referenced in code-behind that do not exist in XAML
$csMissing = @()
foreach ($n in $names) {
    # keep track separately; reverse check below
}
$csTokens = New-Object System.Collections.Generic.HashSet[string]
foreach ($m in [regex]::Matches($cs, '\b([A-Z][A-Za-z0-9_]{2,})\b')) { [void]$csTokens.Add($m.Groups[1].Value) }
foreach ($n in $names) { if (-not $csTokens.Contains($n)) { } }

# reverse: which XAML names are used in cs (informational)
$usedInCs = @(); $notUsedInCs = @()
foreach ($n in $names) { if ($cs -match ('\b' + [regex]::Escape($n) + '\b')) { $usedInCs += $n } else { $notUsedInCs += $n } }
Write-Output ('XAML-NAMES-NOT-USED-IN-CS: ' + $(if ($notUsedInCs.Count) { $notUsedInCs -join ', ' } else { 'NONE' }))

# --- event handlers wired in XAML vs methods in cs ---
$handlerAttrs = @('Click','Checked','Unchecked','ValueChanged','PreviewMouseLeftButtonDown','PreviewMouseLeftButtonUp','LostMouseCapture','Loaded','Unloaded')
$handlers = @{}
foreach ($el in $doc.SelectNodes('//*')) {
    foreach ($a in $el.Attributes) {
        if ($handlerAttrs -contains $a.Name -and $a.Value -and $a.Value -notmatch '\{') {
            $handlers[$a.Value] = $a.Name
        }
    }
}
Write-Output ''
Write-Output ('XAML-HANDLERS (' + $handlers.Count + '):')
$missingHandlers = @()
foreach ($h in ($handlers.Keys | Sort-Object)) {
    $ok = $cs -match ('void\s+' + [regex]::Escape($h) + '\s*\(')
    Write-Output ('  ' + $h + '  ->  ' + $(if ($ok) { 'FOUND-IN-CS' } else { 'MISSING-IN-CS' }))
    if (-not $ok) { $missingHandlers += $h }
}
Write-Output ('HANDLER-MISSING-COUNT: ' + $missingHandlers.Count)
