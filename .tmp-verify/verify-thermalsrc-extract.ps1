# 温度数据源工作包自检彩排：用 PowerShell/.NET 原样演练 ValidateAndInstall 的算法
# （大小闸 → 开包 → 根级 exe 条目 → 逐条目 SafeEntryTarget 同款收敛 → 覆盖解压 → 终验）。
# 样本一：真实下载的官方 v0.9.4 便携包（2026-11-07 于 /tmp/lhm-verify-094 下载校验）。
# 样本二：本地构造的 zip-slip 恶意 zip（含 ../escape.txt 条目，CreateEntry 允许创建）。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Test-EntryPathIsSafe([string]$entryName) {
    if ([string]::IsNullOrWhiteSpace($entryName)) { return $false }
    $normalized = $entryName.Replace('\', '/')
    if ($normalized.StartsWith('/')) { return $false }
    foreach ($segment in $normalized.Split('/')) {
        if ($segment.Length -eq 0) { return $false }
        if ($segment -eq '.' -or $segment -eq '..') { return $false }
        if ($segment.EndsWith(':') -or $segment.Contains(':')) { return $false }
    }
    return $true
}

function Get-SafeEntryTarget([string]$installDir, [string]$entryName) {
    $normalized = $entryName.Replace('\', '/').TrimEnd('/')
    if (-not (Test-EntryPathIsSafe $normalized)) { return $null }
    $target = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($installDir, $normalized.Replace('/', [System.IO.Path]::DirectorySeparatorChar)))
    $root = [System.IO.Path]::GetFullPath($installDir).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    if (-not $target.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) -and
        $target -ne $root) { return $null }
    return $target
}

function Invoke-ValidateAndInstall([string]$zipPath, [string]$installDir, [long]$expectedBytes, [bool]$enforceSize) {
    $actual = (Get-Item -LiteralPath $zipPath).Length
    if ($enforceSize -and $actual -ne $expectedBytes) {
        return @{ Ok = $false; Why = "SizeMismatch actual=$actual expected=$expectedBytes" }
    }
    $archive = $null
    try {
        try { $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath) }
        catch { return @{ Ok = $false; Why = 'NotAnArchive' } }
        $exeEntry = $archive.Entries | Where-Object { $_.FullName -ieq 'LibreHardwareMonitor.exe' }
        if (-not $exeEntry) { return @{ Ok = $false; Why = 'MissingExecutable' } }
        New-Item -ItemType Directory -Force -Path $installDir | Out-Null
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.EndsWith('/')) {
                $dir = Get-SafeEntryTarget $installDir $entry.FullName
                if (-not $dir) { return @{ Ok = $false; Why = "UnsafeEntry $($entry.FullName)" } }
                New-Item -ItemType Directory -Force -Path $dir | Out-Null
                continue
            }
            $target = Get-SafeEntryTarget $installDir $entry.FullName
            if (-not $target) { return @{ Ok = $false; Why = "UnsafeEntry $($entry.FullName)" } }
            $parent = Split-Path -Parent $target
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
        $exePath = Join-Path $installDir 'LibreHardwareMonitor.exe'
        if (-not (Test-Path -LiteralPath $exePath)) { return @{ Ok = $false; Why = 'NoExeAfterExtract' } }
        return @{ Ok = $true; Why = 'Installed'; Exe = $exePath; ExeBytes = (Get-Item -LiteralPath $exePath).Length }
    }
    finally { if ($archive) { $archive.Dispose() } }
}

# ---------- 样本一：真实官方 v0.9.4 ----------
$officialZip = 'C:/Users/chenk/AppData/Local/Temp/lhm-verify-094/LibreHardwareMonitor-net472.zip'
$installDir1 = 'C:/Users/chenk/AppData/Local/Temp/lhm-rehearsal-install/LibreHardwareMonitor'
if (Test-Path $installDir1) { Remove-Item -Recurse -Force $installDir1 }
$r1 = Invoke-ValidateAndInstall $officialZip $installDir1 1930741 $true
Write-Output ("official: Ok=" + $r1.Ok + " Why=" + $r1.Why + " ExeBytes=" + $r1.ExeBytes)
Write-Output ("official-satellite: " + (Test-Path (Join-Path $installDir1 'de/Microsoft.Win32.TaskScheduler.resources.dll')))

# ---------- 样本二：zip-slip 恶意 zip（本地构造） ----------
$slipZip = 'C:/Users/chenk/AppData/Local/Temp/zipslip-test.zip'
$ms = New-Object System.IO.MemoryStream
$zip = New-Object System.IO.Compression.ZipArchive($ms, [System.IO.Compression.ZipArchiveMode]::Create, $true)
$e1 = $zip.CreateEntry('../escape.txt'); $w = New-Object System.IO.StreamWriter($e1.Open()); $w.Write('evil'); $w.Close()
$e2 = $zip.CreateEntry('LibreHardwareMonitor.exe'); $w = New-Object System.IO.StreamWriter($e2.Open()); $w.Write('fake'); $w.Close()
$zip.Dispose()
$fs = [System.IO.File]::Create($slipZip); $ms.Position = 0; $ms.CopyTo($fs); $fs.Close(); $ms.Dispose()
$installDir2 = 'C:/Users/chenk/AppData/Local/Temp/lhm-rehearsal-slip/LibreHardwareMonitor'
if (Test-Path $installDir2) { Remove-Item -Recurse -Force $installDir2 }
$escaped = 'C:/Users/chenk/AppData/Local/Temp/lhm-rehearsal-slip/escape.txt'
if (Test-Path $escaped) { Remove-Item -Force $escaped }
$r2 = Invoke-ValidateAndInstall $slipZip $installDir2 0 $false
Write-Output ("zipslip: Ok=" + $r2.Ok + " Why=" + $r2.Why)
Write-Output ("zipslip-escaped-file-created: " + (Test-Path $escaped))

# ---------- 附带：根级 exe 二进制里的 WMI 命名空间（v0.9.4 裁定依据复跑） ----------
$found = $false
$archive3 = [System.IO.Compression.ZipFile]::OpenRead($officialZip)
try {
    $exe = $archive3.Entries | Where-Object { $_.FullName -ieq 'LibreHardwareMonitor.exe' }
    $ms3 = New-Object System.IO.MemoryStream
    $exe.Open().CopyTo($ms3)
    $ascii = [System.Text.Encoding]::ASCII.GetString($ms3.ToArray())
    $found = $ascii.Contains('root/LibreHardwareMonitor')
} finally { $archive3.Dispose() }
Write-Output ("official-exe-contains-wmi-namespace: " + $found)
