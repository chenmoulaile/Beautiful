using Beautiful.App.MediaWidget;
using Beautiful.App.Services;

namespace Beautiful.App.TopBar;

/// <summary>
/// 顶栏依赖注入容器（Task 9）：App 组合根在 <c>EnsureTopBar</c> 构造并注入 <see cref="TopBarWindow"/>。
/// 顶栏不持有 Dock/托盘生命周期，只经此拿到"窗口跟踪器 / 媒体 VM / 系统动作回调"。
/// Dock 缺席（降级为无 Dock 运行）时 <see cref="Tracker"/>/<see cref="Media"/> 返回 null，
/// 相应状态项与"窗口"菜单自动隐藏/置空。委托均为 UI 线程调用语义。
/// </summary>
public sealed record TopBarContext
{
    /// <summary>取当前 Dock 的窗口跟踪器（"窗口"菜单数据源 + 激活动作）；无 Dock 时 null。</summary>
    public required Func<WindowTracker?> Tracker { get; init; }

    /// <summary>取当前 Dock 的媒体 VM（媒体迷你项状态源）；无 Dock 时 null。</summary>
    public required Func<MediaViewModel?> Media { get; init; }

    /// <summary>点击媒体迷你项：展开 Dock 媒体面板。</summary>
    public required Action OpenMediaPanel { get; init; }

    /// <summary>设置…：打开设置窗口（复用 App.OpenSettings）。</summary>
    public required Action OpenSettings { get; init; }

    /// <summary>启动器（v3 T7）：开/收 Spotlight 式搜索面板（复用 App 的启动器服务，与热键同一入口）。</summary>
    public required Action OpenLauncher { get; init; }

    /// <summary>重载 Dock（复用 App.ReloadDock；顶栏随之重建）。</summary>
    public required Action ReloadDock { get; init; }

    /// <summary>退出（复用 App.ExitApp 清理链路）。</summary>
    public required Action Exit { get; init; }

    /// <summary>主题标识落地（复用 App.ApplyThemeFromTray 同一持久化路径）。</summary>
    public required Action<string> ApplyTheme { get; init; }

    /// <summary>当前主题标识（主题子菜单勾选态）。</summary>
    public required Func<string> CurrentTheme { get; init; }
}
