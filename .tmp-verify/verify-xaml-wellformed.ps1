# XAML well-formed check (parse only, no build) - ASCII only, no BOM issues.
$files = @(
    'D:/Beautiful/src/Beautiful.App/Dock/DockWindow.xaml',
    'D:/Beautiful/src/Beautiful.App/Settings/SettingsWindow.xaml'
)
foreach ($f in $files) {
    try {
        $x = New-Object System.Xml.XmlDocument
        $x.Load($f)
        Write-Output "OK   $f"
    } catch {
        Write-Output "FAIL $f : $($_.Exception.Message)"
    }
}
