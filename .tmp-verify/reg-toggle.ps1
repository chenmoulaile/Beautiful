# reg-toggle.ps1 - toggle WPF DisableHWAcceleration for Beautiful.exe (ASCII only)
param([string]$Action = "set")
$key = "HKCU:\Software\Microsoft\Avalon.Graphics\Beautiful.exe"
if ($Action -eq "set") {
    if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    Set-ItemProperty -Path $key -Name DisableHWAcceleration -Value 1 -Type DWord
    Write-Output "REG_SET DisableHWAcceleration=1"
} elseif ($Action -eq "clear") {
    if (Test-Path $key) { Remove-Item -Path $key -Recurse -Force }
    Write-Output "REG_CLEARED"
} else {
    if (Test-Path $key) { Get-ItemProperty -Path $key | Out-String } else { Write-Output "REG_ABSENT" }
}
