# Beautiful 一键发布（本地半程）：版本号 → 工作树检查 → 构建 + 测试门禁（不绿不发布）→
#   提交 chore(release) → 打 v* 标签并推送。推送到远端即触发 .github/workflows/release.yml，
#   由 **CI 独占** Release 创建与资产（zip）上传。
# 用法（在仓库根目录）:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release.ps1
#   powershell ... -File scripts\release.ps1 -Version 2.1.0     # 同时改写 Directory.Build.props 的 <Version>
# 依赖：本机 git 凭据（Git Credential Manager）对 github.com 有 repo 权限。
#
# 【终审 M-4：资产归属选择】本地脚本与 CI 曾同时向同一 Release 上传**同名**资产：
# GitHub 会拒绝重复资产名，且两条路径互相竞态（谁先传完谁占名，另一个报错或被覆盖），
# 表现为"Release 有时没有 zip / 脚本红着退出"。现裁定：**CI 独占 Release 资产**——本地
# 只负责"门禁 + tag + push"，push 成功即视为发布委托完成，产物由 Actions 构建上传。
# 好处：本机不再需要 GitHub API 令牌路径，也消除了双写。
# 【终审 M-3：git push 失败必须可见】$ErrorActionPreference='Stop' 管不到原生命令的 stderr：
# git push 被拒（非快进、凭据失效、网络）时脚本会照常往下走，制造"tag 在本地、Release
# 永远等不到 CI"的静默半发布。每个 push 后显式检查 $LASTEXITCODE。
param(
    [string]$Version
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

# 4) 提交版本号（若变化）并打 tag、推送（推送即委托 CI 完成 Release 与资产上传）
Step "提交版本号与标签 $tag"
git add Directory.Build.props
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) { git commit -m "chore(release): $tag"; if ($LASTEXITCODE -ne 0) { throw '版本号提交失败' } }
if (git tag -l $tag) { Write-Host "标签 $tag 已存在，跳过创建" } else {
    git tag -a $tag -m "Beautiful $tag"
    if ($LASTEXITCODE -ne 0) { throw "打标签 $tag 失败" }
}

# 【M-3】git push 失败不会抛异常（native stderr 不受 ErrorActionPreference 约束），必须查退出码：
# 否则出现"Release 建了、CI 从没跑起来"或"本地自认为发布完成、远端根本没这个 tag"的半发布。
Step '推送分支与标签（触发云端 CI 发布）'
git push origin HEAD
if ($LASTEXITCODE -ne 0) { throw "git push origin HEAD 失败（退出码 $LASTEXITCODE）：分支未上远端，中止——不要手工创建 Release" }
git push origin $tag
if ($LASTEXITCODE -ne 0) { throw "git push origin $tag 失败（退出码 $LASTEXITCODE）：tag 未上远端，CI 不会触发，本地可重试 git push origin $tag" }

Step "完成：tag $tag 已推送，GitHub Actions（release.yml）将构建并创建/更新 Release 与资产"
$remoteUrl = git remote get-url origin
if ($remoteUrl -match 'github\.com[:/]([^/]+)/([^/.]+?)(\.git)?$') {
    Write-Host "    进度见：https://github.com/$($Matches[1])/$($Matches[2])/actions（Release 页面稍后出现 Beautiful-win-x64-$tag.zip）"
} else {
    Write-Host "    请到远端 Actions 页面查看发布进度（zip 资产由 CI 上传）"
}
