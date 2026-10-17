using Microsoft.Win32;
using Beautiful.Core.Settings;
using Beautiful.Core.Themes;

namespace Beautiful.App.Services;

/// <summary>
/// 设置门面：静态 Current（AppSettings 不可变记录，变更以 with 生成新值后 Update）+
/// Load/Save（config/settings.json，损坏自愈）+ SetAutostart（HKCU Run）。注册表与
/// settings.json 双写：JSON 持久意图，注册表是系统实际生效项。
/// </summary>
public static class SettingsService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Beautiful";

    /// <summary>启动时 Load 后即生效；设置窗口经 Update/UpdateAndSave 变更。</summary>
    public static AppSettings Current { get; private set; } = new();

    public static void Load() => Current = new AppSettingsStore().Load();

    public static void Save() => new AppSettingsStore().Save(Current);

    /// <summary>
    /// 替换当前设置（不落盘；配合防抖 Save 使用）。写入值统一过 <see cref="AppSettings.Normalized"/>：
    /// 内存态 Current 与落盘态从此同源，滑杆等控件即便写出越界值（如 DockIconSize=200）也即时钳制，
    /// 不会出现"当前渲染用 200、重启后变 72"的不一致（Task 1 评审遗留，本任务一并修复）。
    /// </summary>
    public static void Update(AppSettings settings) => Current = settings.Normalized();

    /// <summary>替换当前设置并立即落盘（离散控件用）。同样先规范化再存，读写一致。</summary>
    public static void UpdateAndSave(AppSettings settings)
    {
        Current = settings.Normalized();
        Save();
    }

    /// <summary>
    /// 开机自启：HKCU\...\Run 值 "Beautiful" = "\"exe路径\" --minimized"
    /// （带引号防路径空格；--minimized 标识自启来源，壳层启动时仅记录——Dock 即主 UI 照常显示）。
    /// 关闭即删除值。
    /// </summary>
    public static void SetAutostart(bool enabled)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (enabled && string.IsNullOrEmpty(exe))
            {
                Logger.Warn("SetAutostart: 无法确定可执行文件路径，跳过");
                return;
            }
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled) key.SetValue(RunValueName, $"\"{exe}\" --minimized");
            else key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            // 注册表操作失败不拖垮启动/设置流程（下次保存可重试）
            Logger.Warn($"设置开机自启失败: {ex.Message}");
        }
    }

    /// <summary>设置主题标识 → 主题定义（未知值兜底鸿蒙；AppSettings.Normalized 已收敛，双保险）。</summary>
    public static ThemeDefinition ThemeDefinitionOf(AppSettings s) => s.Theme switch
    {
        "macos" => ThemeDefinition.MacOs(),
        "linux" => ThemeDefinition.Linux(),
        "windows" => ThemeDefinition.Windows(),
        _ => ThemeDefinition.HarmonyOs()
    };

    /// <summary>设置策略标识 → TaskbarPolicy（未知值兜底 hide，与默认一致）。</summary>
    public static TaskbarPolicy PolicyOf(AppSettings s) => s.TaskbarPolicy switch
    {
        "autohide" => TaskbarPolicy.AutoHide,
        "keep" => TaskbarPolicy.Keep,
        _ => TaskbarPolicy.Hide
    };
}
