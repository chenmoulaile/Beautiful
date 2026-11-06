$ErrorActionPreference = 'Stop'
$repo = 'D:/Beautiful'
$files = @(
    'src/Beautiful.App/Assets/Symbols/Symbols.xaml',
    'src/Beautiful.App/Settings/ThermalSettingsCard.xaml'
)
foreach ($f in $files) {
    $path = Join-Path $repo $f
    try {
        [xml]$doc = Get-Content -Path $path -Raw -Encoding UTF8
        Write-Output "OK-WELLFORMED: $f"
    }
    catch {
        Write-Output "FAIL: $f :: $($_.Exception.Message)"
    }
}
