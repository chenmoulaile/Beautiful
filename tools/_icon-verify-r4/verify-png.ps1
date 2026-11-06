# PNG verify: real PNG magic + expected size (default 1024x1024).
# Usage: verify-png.ps1 [-Expected] 1024 file1.png file2.png ...
# Output per line: path|WxH|OK  or  path|reason|FAIL*. Exit 1 if any FAIL.
param(
    [int]$Expected = 1024,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Paths
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$fail = $false
foreach ($path in $Paths) {
    try {
        if (-not (Test-Path -LiteralPath $path)) {
            Write-Output "$path|file missing|FAIL"
            $fail = $true
            continue
        }
        # PNG magic: 89 50 4E 47 0D 0A 1A 0A
        $head = [System.IO.File]::ReadAllBytes($path)[0..7]
        $magicOk = ($head[0] -eq 0x89) -and ($head[1] -eq 0x50) -and ($head[2] -eq 0x4E) -and ($head[3] -eq 0x47) -and ($head[4] -eq 0x0D) -and ($head[5] -eq 0x0A) -and ($head[6] -eq 0x1A) -and ($head[7] -eq 0x0A)
        if (-not $magicOk) {
            Write-Output "$path|bad PNG magic|FAIL"
            $fail = $true
            continue
        }
        $img = [System.Drawing.Image]::FromFile($path)
        try {
            $size = "$($img.Width)x$($img.Height)"
            if ($img.Width -eq $Expected -and $img.Height -eq $Expected) {
                Write-Output "$path|$size|OK"
            } else {
                Write-Output "$path|$size|FAIL-size"
                $fail = $true
            }
        } finally {
            $img.Dispose()
        }
    } catch {
        Write-Output "$path|exception $($_.Exception.GetType().Name) $($_.Exception.Message)|FAIL"
        $fail = $true
    }
}
if ($fail) { exit 1 } else { exit 0 }
