namespace Beautiful.Core.Settings;

/// <summary>
/// 应用设置（config/settings.json）。固定应用不在此处（控制器裁定：PinnedApps 只存在于
/// config/dock.json，由 App 侧 PinnedAppsStore 管理）。首次 Save 才落盘，文件缺失时一律代码默认。
/// </summary>
public sealed record AppSettings
{
    /// <summary>主题标识："harmonyos" | "macos" | "linux"。</summary>
    public string Theme { get; init; } = "harmonyos";

    /// <summary>任务栏策略："hide" | "autohide" | "keep"。</summary>
    public string TaskbarPolicy { get; init; } = "hide";

    /// <summary>开机自启（HKCU Run "Beautiful"）。</summary>
    public bool Autostart { get; init; }

    /// <summary>歌词对齐偏移（毫秒，-2000..2000，加载时钳制）。</summary>
    public int LyricsOffsetMs { get; init; }

    /// <summary>Dock 是否显示媒体胶囊条目。</summary>
    public bool ShowMediaCapsule { get; init; } = true;

    /// <summary>媒体面板失焦是否自动收起。</summary>
    public bool HidePanelOnDeactivate { get; init; } = true;

    /// <summary>合法主题标识（小写）。</summary>
    public static IReadOnlyList<string> KnownThemes { get; } = ["harmonyos", "macos", "linux"];

    /// <summary>合法任务栏策略标识（小写）。</summary>
    public static IReadOnlyList<string> KnownTaskbarPolicies { get; } = ["hide", "autohide", "keep"];

    /// <summary>
    /// 加载侧规范化：偏移钳制到 -2000..2000；未知主题/策略标识（含大小写差异）收敛为默认，
    /// 避免手改配置把 App 侧映射打到兜底分支之外的未定义行为。
    /// </summary>
    public AppSettings Normalized() => this with
    {
        LyricsOffsetMs = Math.Clamp(LyricsOffsetMs, -2000, 2000),
        Theme = KnownThemes.Contains(Theme.ToLowerInvariant()) ? Theme.ToLowerInvariant() : "harmonyos",
        TaskbarPolicy = KnownTaskbarPolicies.Contains(TaskbarPolicy.ToLowerInvariant())
            ? TaskbarPolicy.ToLowerInvariant()
            : "hide"
    };
}
