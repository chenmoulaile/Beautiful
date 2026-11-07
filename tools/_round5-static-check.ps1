# 残项清零（alpha.29）静态自查脚本：只读检查，不编译。
# ① 改动/新增 XAML 的 XML 良构校验（[xml] 加载）；② 改动 .cs 花括号配平；③ 接线符号 grep。
$ErrorActionPreference = "Stop"
Set-Location "D:/Beautiful"

$xmaml = @(
  "src/Beautiful.App/Settings/SettingsWindow.xaml",
  "src/Beautiful.App/Settings/IconThemeEditorControl.xaml",
  "src/Beautiful.App/Themes/Aurora.xaml",
  "src/Beautiful.App/Themes/MacOS.xaml",
  "src/Beautiful.App/Switcher/SwitcherWindow.xaml"
)
Write-Output "=== 1. XAML 良构（XmlDocument.Load）==="
foreach ($f in $xmaml) {
  try {
    $doc = New-Object System.Xml.XmlDocument
    $doc.Load((Resolve-Path $f).Path)
    Write-Output ("OK   " + $f)
  } catch {
    Write-Output ("FAIL " + $f + " : " + $_.Exception.Message)
  }
}

$csfiles = @(
  "src/Beautiful.Core/Icons/LetterTile.cs",
  "src/Beautiful.Core/Layout/ScreenCornerMonitors.cs",
  "src/Beautiful.Core/Layout/ScreenCorners.cs",
  "src/Beautiful.Core/Launcher/LauncherHotkey.cs",
  "src/Beautiful.Core/Settings/AppSettings.cs",
  "src/Beautiful.App/Services/LetterTileRenderer.cs",
  "src/Beautiful.App/Launcher/LauncherService.cs",
  "src/Beautiful.App/Switcher/SwitcherHotkeyService.cs",
  "src/Beautiful.App/Switcher/SwitcherService.cs",
  "src/Beautiful.App/Switcher/SwitcherWindow.xaml.cs",
  "src/Beautiful.App/Switcher/AltTabHook.cs",
  "src/Beautiful.App/ScreenCorners/ScreenCornerService.cs",
  "src/Beautiful.App/Settings/SettingsWindow.xaml.cs",
  "src/Beautiful.App/Themes/ThemeManager.cs",
  "src/Beautiful.App/App.xaml.cs",
  "tests/Beautiful.Core.Tests/LetterTileTests.cs",
  "tests/Beautiful.Core.Tests/LauncherHotkeyTests.cs",
  "tests/Beautiful.Core.Tests/ScreenCornerSettingsTests.cs"
)
Write-Output "=== 2. 花括号配平（{} 计数相等；冒烟非语法证明）==="
foreach ($f in $csfiles) {
  $text = [System.IO.File]::ReadAllText((Resolve-Path $f).Path)
  $open = ([regex]::Matches($text, "\{")).Count
  $close = ([regex]::Matches($text, "\}")).Count
  $ok = ($open -eq $close)
  Write-Output ((("{0,-4}" -f "OK") + " $f  open=$open close=$close").Replace("OK", $(if ($ok) { "OK" } else { "FAIL" })))
}

Write-Output "=== 3. 接线符号（计数应 > 0）==="
$checks = @(
  @("IconOf 首字母色块兜底",        "src/Beautiful.App/Launcher/LauncherService.cs", "LetterTileRenderer.Render"),
  @("ThemeManager accent 缓存",     "src/Beautiful.App/Themes/ThemeManager.cs",      "AccentHex = accent"),
  @("呼出热键服务装配",             "src/Beautiful.App/Switcher/SwitcherService.cs", "ApplyOpenHotkey"),
  @("App 启动序接呼出热键",         "src/Beautiful.App/App.xaml.cs",                 "ApplyOpenHotkey"),
  @("--switcher 取证开关",          "src/Beautiful.App/App.xaml.cs",                 '"--switcher"'),
  @("App 改键入口",                 "src/Beautiful.App/App.xaml.cs",                 "ApplySwitcherOpenHotkey"),
  @("设置页呼出热键处理器",         "src/Beautiful.App/Settings/SettingsWindow.xaml.cs", "SwitcherOpenHotkey_Apply"),
  @("设置页遮罩色处理器",           "src/Beautiful.App/Settings/SettingsWindow.xaml.cs", "ScreenCornerMaskColor_Changed"),
  @("设置页排除屏处理器",           "src/Beautiful.App/Settings/SettingsWindow.xaml.cs", "ScreenCornersExclude_Changed"),
  @("服务按排除名单过滤",           "src/Beautiful.App/ScreenCorners/ScreenCornerService.cs", "ScreenCornerMonitors.IsExcluded"),
  @("grave 键码映射",               "src/Beautiful.App/Switcher/AltTabHook.cs",      "VkOem3"),
  @("graves 主键白名单",            "src/Beautiful.Core/Launcher/LauncherHotkey.cs", '"grave"'),
  @("Aurora 注册-设置白名单",       "src/Beautiful.Core/Settings/AppSettings.cs",    '"macos", "aurora"'),
  @("Aurora 注册-主题定义",         "src/Beautiful.App/Services/SettingsService.cs", "ThemeDefinition.Aurora()"),
  @("Aurora 注册-控制中心分段源",   "src/Beautiful.Core/Controls/QuickSettings.cs",  'new("aurora", "Aurora")'),
  @("Aurora 注册-ApplyTokens 分支", "src/Beautiful.App/Themes/ThemeManager.cs",      "AuroraTokenDictionary.Apply"),
  @("图集格子显式样式",             "src/Beautiful.App/Settings/IconThemeEditorControl.xaml", "GalleryCellButton")
)
foreach ($c in $checks) {
  $count = ([regex]::Matches(([System.IO.File]::ReadAllText((Resolve-Path $c[1]).Path)), [regex]::Escape($c[2]))).Count
  $verdict = if ($count -gt 0) { "OK" } else { "FAIL" }
  Write-Output ("$verdict  $($c[0])  hits=$count  in=$($c[1])")
}
