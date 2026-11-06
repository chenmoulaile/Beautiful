# anchor-mouse.ps1 - move cursor to physical (1440,800), screen center (ASCII only)
param([int]$X = 1440, [int]$Y = 800)
Add-Type -TypeDefinition @"
using System.Runtime.InteropServices;
public class M { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); }
"@
[M]::SetProcessDPIAware() | Out-Null
$ok = [M]::SetCursorPos($X, $Y)
Write-Output "CURSOR_AT $X,$Y ok=$ok"
