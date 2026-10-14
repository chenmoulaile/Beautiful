# Beautiful 一键发布：测试门禁 → 构建自包含产物 → 打 zip → 打 tag 推送 → 创建 GitHub Release 并上传资产。
# 用法（在仓库根目录）:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release.ps1
#   powershell ... -File scripts\release.ps1 -Version 2.1.0     # 同时改写 Directory.Build.props 的 <Version>
# 依赖：本机 git 凭据（Git Credential Manager）对 github.com 有 repo 权限。
param(
    [string]$Version,
    [switch]$SkipPublish   # 只建 Release 不重新打包（调试用）
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# 仓库内自带 SDK 与 NuGet 缓存（用户要求：不写 C 盘）
$env:DOTNET_ROOT = Join-Path $root '.dotnet'
$env:NUGET_PACKAGES = Join-Path $root '.nuget'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }

# 1) 版本号：参数优先，否则读 Directory.Build.props 的 <Version>
$propsPath = Join-Path $root 'Directory.Build.props'
if (-not $Version) {
    $Version = (Select-String -Path $propsPath -Pattern '<Version>([^<]+)</Version>' -ErrorAction Stop).Matches[0].Groups[1].Value
    Step "使用现有版本号 $Version"
} else {
    Step "改写版本号为 $Version"
    (Get-Content $propsPath -Raw) -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>" | Set-Content $propsPath -NoNewline -Encoding UTF8
}
$tag = "v$Version"

# 2) 工作树必须干净（除本次版本号改动外）
Step '检查工作树'
$dirty = git status --porcelain -- . ':!Directory.Build.props'
if ($dirty) { throw "工作树有未提交改动，先提交后再发布：`n$dirty" }

# 3) 测试门禁：不绿就不发布
Step '构建 + 测试门禁'
dotnet build Beautiful.sln -c Release | Out-Host
if ($LASTEXITCODE -ne 0) { throw '构建失败' }
dotnet test tests/Beautiful.Core.Tests -c Release | Out-Host
if ($LASTEXITCODE -ne 0) { throw '测试未通过，拒绝发布' }

# 4) 提交版本号（若变化）并打 tag、推送
Step "提交版本号与标签 $tag"
git add Directory.Build.props
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) { git commit -m "chore(release): $tag" }
if (git tag -l $tag) { Write-Host "标签 $tag 已存在，跳过创建" } else { git tag -a $tag -m "Beautiful $tag" }
git push origin HEAD
git push origin $tag

# 5) 打包自产物
$zipPath = Join-Path $env:TEMP "Beautiful-win-x64-$Version.zip"
if (-not $SkipPublish) {
    $out = Join-Path $root 'publish'
    Step "发布自包含产物 → $out"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    dotnet publish src/Beautiful.App -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:UseAppHost=true -o $out | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }
    Step '压缩产物'
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zipPath -CompressionLevel Optimal
    $mb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Host "    zip: $zipPath ($mb MB)"
}

# 6) 取 GitHub 令牌（走 Git Credential Manager，不提示输入）
Step '获取 GitHub 凭据'
$cred = "protocol=https`nhost=github.com`n`n" | git credential fill
$token = ($cred | Select-String '^password=').ToString().Substring('password='.Length)
if (-not $token) { throw '未能从 Git Credential Manager 取得令牌' }
$api = 'https://api.github.com'
$headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'User-Agent' = 'beautiful-release-script' }

# 解析仓库归属（从 remote URL 取 owner/repo，避免硬编码）
$remoteUrl = (git remote get-url origin)
if ($remoteUrl -match 'github\.com[:/]([^/]+)/([^/.]+?)(\.git)?$') { $owner = $Matches[1]; $repo = $Matches[2] }
else { throw "无法从 remote 解析 owner/repo：$remoteUrl" }
Step "目标仓库 $owner/$repo"

$body = @"
Beautiful $tag — Windows 11 悬浮 Dock：macOS/Linux/鸿蒙三主题 + macOS 顶栏 + SMTC 媒体控制与双语歌词 + Alt+Tab 替换切换器。

下载 ``Beautiful-win-x64-$Version.zip``，解压后运行 ``Beautiful.exe``（自包含，无需另装 .NET 运行时）。
完整变更见仓库提交历史与 README。
"@

# 7) 创建（或复用）Release
Step '创建 Release'
$existing = Invoke-RestMethod -Uri "$api/repos/$owner/$repo/releases/tags/$tag" -Headers $headers -ErrorAction SilentlyContinue
if ($existing) {
    $releaseId = $existing.id
    Write-Host "    Release 已存在 id=$releaseId，更新之"
    $patch = @{ tag_name = $tag; name = $tag; body = $body; draft = $false; prerelease = $false } | ConvertTo-Json
    Invoke-RestMethod -Method Patch -Uri "$api/repos/$owner/$repo/releases/$releaseId" -Headers $headers -ContentType 'application/json' -Body $patch | Out-Null
} else {
    $payload = @{ tag_name = $tag; name = $tag; body = $body; draft = $false; prerelease = $false } | ConvertTo-Json
    $rel = Invoke-RestMethod -Method Post -Uri "$api/repos/$owner/$repo/releases" -Headers $headers -ContentType 'application/json' -Body $payload
    $releaseId = $rel.id
    Write-Host "    已创建 Release id=$releaseId"
}

# 8) 上传资产（同名先删，保证可重复运行）
if (Test-Path $zipPath) {
    Step '上传资产'
    $assets = (Invoke-RestMethod -Uri "$api/repos/$owner/$repo/releases/$releaseId/assets" -Headers $headers)
    foreach ($a in $assets) {
        if ($a.name -eq "Beautiful-win-x64-$Version.zip") {
            Invoke-RestMethod -Method Delete -Uri $a.url -Headers $headers | Out-Null
        }
    }
    $upload = "https://uploads.github.com/repos/$owner/$repo/releases/$releaseId/assets?name=Beautiful-win-x64-$Version.zip"
    $bytes = [System.IO.File]::ReadAllBytes($zipPath)
    Invoke-RestMethod -Method Post -Uri $upload -Headers $headers -ContentType 'application/zip' -Body $bytes | Out-Null
    Write-Host "    已上传 Beautiful-win-x64-$Version.zip"
}

Step "完成：https://github.com/$owner/$repo/releases/tag/$tag"
