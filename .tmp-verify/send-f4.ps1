# send-f4.ps1 - press F4 (Launchpad global hotkey) then wait (ASCII only)
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.SendKeys]::SendWait("{F4}")
Write-Output "F4_SENT"
