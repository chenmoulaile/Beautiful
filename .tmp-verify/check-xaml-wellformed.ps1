# XAML well-formedness check. Files are UTF-8 without BOM, so read with -Encoding UTF8
# (PS 5.1 default ANSI read would mojibake CJK strings and report bogus parse errors).
$files = @(
    'D:/Beautiful/src/Beautiful.App/Finder/QuickLookWindow.xaml',
    'D:/Beautiful/src/Beautiful.App/Launcher/LauncherOverlay.xaml'
)
foreach ($f in $files) {
    try {
        [xml](Get-Content -Raw -Encoding UTF8 $f) | Out-Null
        Write-Output ("{0}: WELL-FORMED" -f $f)
    } catch {
        Write-Output ("{0}: FAIL - {1}" -f $f, $_.Exception.Message)
    }
}
