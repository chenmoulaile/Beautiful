# capture.ps1 - DPI-aware full-screen capture to -Out path (ASCII only)
param(
    [Parameter(Mandatory = $true)][string]$Out
)
Add-Type -TypeDefinition @"
using System.Runtime.InteropServices;
public class DpiHelper {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[DpiHelper]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$bmp.Save($Out)
$g.Dispose()
$bmp.Dispose()
Write-Host "SHOT_SAVED $Out ($($b.Width)x$($b.Height))"
