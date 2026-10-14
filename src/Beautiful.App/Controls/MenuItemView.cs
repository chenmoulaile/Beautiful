// 自绘菜单，视觉对标 macOS；无第三方代码。
using System.Collections.Generic;
using System.Windows.Media;

namespace Beautiful.App.Controls;

/// <summary>
/// 菜单项视图模型（不可变快照语义）：弹层打开时由调用方构建成一棵 <see cref="Children"/> 树，
/// 菜单生命周期内不再变更——勾选态等"打开时刷新"语义由"每次 Show 前重建列表"实现（与 v1
/// ContextMenuStrip.Opening 刷新等价，但无状态残留）。绑定为一次性读取，无需 INotifyPropertyChanged。
/// </summary>
public sealed class MenuItemView
{
    /// <summary>稳定标识（调试/埋点用，可空串）。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>左侧图标列字形（Segoe Fluent Icons / Segoe MDL2 码点字符）；null = 留空保持对齐。</summary>
    public string? Glyph { get; init; }

    /// <summary>
    /// 左侧图标列位图（优先于 <see cref="Glyph"/>，用于"窗口"菜单展示真实窗口图标）。
    /// null = 该列改渲染字形（或留空）。冻结的 BitmapSource 可跨弹层安全复用。
    /// </summary>
    public ImageSource? Icon { get; init; }

    /// <summary>是否有位图图标（绑定渲染：有图则隐藏字形列内容、显示 Image）。</summary>
    public bool HasIcon => Icon is not null;

    /// <summary>标题文本。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>勾选态：渲染 ✓（强调色）于勾选列；列常驻占位保证标签对齐。</summary>
    public bool IsChecked { get; init; }

    /// <summary>
    /// 可用性：false = 置灰不可交互（控制器裁定：无对应系统能力的 macOS 风格条目据此渲染为灰色而非假装有功能）。
    /// 置灰项不参与键盘导航/悬停高亮/命中。默认 true。
    /// </summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>分隔线项：仅渲染一条贯穿淡线，不可交互。</summary>
    public bool IsSeparator { get; init; }

    /// <summary>子菜单项（悬停驻留 ~140ms 右侧滑出）；null/空 = 叶子项。</summary>
    public List<MenuItemView>? Children { get; init; }

    /// <summary>点击行为（叶子项）。与 <see cref="FluentMenu"/> 的 onSelect 回调二选一生效（onSelect 优先）。</summary>
    public Action? Invoke { get; init; }

    /// <summary>是否有子菜单（绑定渲染 ▸ 箭头）。</summary>
    public bool HasChildren => Children is { Count: > 0 };
}
