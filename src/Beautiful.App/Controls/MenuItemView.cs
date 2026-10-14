// 自绘菜单，视觉对标 macOS；无第三方代码。
using System.Collections.Generic;

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

    /// <summary>标题文本。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>勾选态：渲染 ✓（强调色）于勾选列；列常驻占位保证标签对齐。</summary>
    public bool IsChecked { get; init; }

    /// <summary>分隔线项：仅渲染一条贯穿淡线，不可交互。</summary>
    public bool IsSeparator { get; init; }

    /// <summary>子菜单项（悬停驻留 ~140ms 右侧滑出）；null/空 = 叶子项。</summary>
    public List<MenuItemView>? Children { get; init; }

    /// <summary>点击行为（叶子项）。与 <see cref="FluentMenu"/> 的 onSelect 回调二选一生效（onSelect 优先）。</summary>
    public Action? Invoke { get; init; }

    /// <summary>是否有子菜单（绑定渲染 ▸ 箭头）。</summary>
    public bool HasChildren => Children is { Count: > 0 };
}
