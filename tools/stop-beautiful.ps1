$p = Get-Process *Beautiful* -ErrorAction SilentlyContinue
if ($p) {
    $p | ForEach-Object { Stop-Process -Id $_.Id -Force }
    Start-Sleep -Seconds 2
    "STOPPED: " + (($p | Select-Object -ExpandProperty Id) -join ',')
} else {
    "NO_BEAUTIFUL_PROCESS"
}
