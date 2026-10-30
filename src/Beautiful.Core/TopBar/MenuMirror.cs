namespace Beautiful.Core.TopBar;

/// <summary>
/// 前台应用 Win32 菜单的镜像节点（v4 Phase 4-T3）。App 侧用 GetMenu 读取并填充，
/// Core 只负责标签清洗与结构数据，便于单测；点击回发由 App 按 <see cref="CommandId"/> 做。
/// </summary>
/// <param name="Label">去掉 &amp; 访问键、不含快捷键的显示文本。</param>
/// <param name="Shortcut">菜单字符串里 \t 之后的快捷键文本；无则 null。</param>
/// <param name="CommandId">WM_COMMAND 的 wParam；0 = 分隔线/无命令。</param>
/// <param name="IsEnabled">未被 MF_GRAYED/MF_DISABLED。</param>
/// <param name="IsChecked">MF_CHECKED。</param>
/// <param name="IsSeparator">MF_SEPARATOR。</param>
/// <param name="Children">子菜单；空 = 叶子。</param>
public sealed record MirrorMenuItem(
    string Label,
    string? Shortcut,
    uint CommandId,
    bool IsEnabled,
    bool IsChecked,
    bool IsSeparator,
    IReadOnlyList<MirrorMenuItem> Children)
{
    public bool HasChildren => Children.Count > 0;
}

/// <summary>Win32 菜单字符串的纯函数处理（访问键 &amp; / 制表符快捷键 / 空标签）。</summary>
public static class MenuMirrorText
{
    /// <summary>
    /// 拆分菜单字符串：第一个 \t 之前是标签，之后是快捷键；去掉 &amp; 访问键并 trim。
    /// 空字符串/只有 &amp; 的项视为分隔线候选（是否分隔由 MF_SEPARATOR 最终决定，不靠文本猜）。
    /// </summary>
    public static (string Label, string? Shortcut) Split(string? raw)
    {
        var text = raw ?? string.Empty;
        var tab = text.IndexOf('\t');
        var label = tab >= 0 ? text[..tab] : text;
        var shortcut = tab >= 0 ? text[(tab + 1)..].Trim() : null;
        label = label.Replace("&&", "\u0001").Replace("&", string.Empty).Replace("\u0001", "&").Trim();
        return (label, string.IsNullOrWhiteSpace(shortcut) ? null : shortcut);
    }
}
