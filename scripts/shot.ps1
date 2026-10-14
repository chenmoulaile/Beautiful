# scripts/shot.ps1 - full-screen capture to repo-root shot.png (smoke verification)
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$bmp.Save("$PSScriptRoot\..\shot.png")
Write-Host "SHOT_SAVED $PSScriptRoot\..\shot.png"
