namespace Beautiful.Core.TopBar;

/// <summary>
/// 顶栏"窗口"菜单的条目文字：把又长又带路径的窗口标题压成一眼能认的一小段。
/// <para>
/// 【为什么要压】Windows 应用的标题常常是"<c>完整路径 + 文件名 • 解决方案 - 编辑器名</c>"这种形态
/// （用户实测的那条 Sublime 标题有 90+ 字符），菜单宽度由最长的一条决定，于是"窗口"菜单横向
/// 铺满小半个屏幕。macOS 的 Window 菜单只写文档名，观感差距就在这里。
/// </para>
/// <para>
/// 规则按"信息量密度"排序，而不是按字符数硬切：编辑器用 <c> • </c> 挂的工程/解决方案尾巴先丢掉
/// （它对"这是哪个窗口"几乎没帮助），路径只留最后一段（文件名才是识别点），最后才截断。
/// 截断保留<b>开头</b>——浏览器类标题的识别信息在前段（"2026 CSP-S 复赛模拟赛 Day 8 - Google Chrome"）。
/// 原文不丢：调用方把它放进条目的 ToolTip。
/// </para>
/// </summary>
public static class WindowMenuLabel
{
    /// <summary>默认最长保留字符数（约等于菜单能容下的舒适宽度）。</summary>
    public const int DefaultMaxChars = 40;

    /// <summary>空标题的占位（不给空字符串，否则那一行看起来像渲染坏了）。</summary>
    public const string Untitled = "（无标题）";

    /// <summary>把窗口标题压成菜单文字。<c>null</c>/空白一律给 <see cref="Untitled"/>。</summary>
    public static string Compact(string? title, int maxChars = DefaultMaxChars)
    {
        if (string.IsNullOrWhiteSpace(title)) return Untitled;

        var text = CollapseWhitespace(title).Trim();

        // 编辑器/IDE 挂在后面的装饰段（" • (Solution)"、" • [Branch]"）先丢
        var bullet = text.IndexOf(" \u2022 ", StringComparison.Ordinal);
        if (bullet > 0) text = text[..bullet].Trim();

        // 带路径的只留最后一段：文件名才是识别点，目录前缀对"这是哪个窗口"没帮助
        var lastSeparator = text.LastIndexOfAny(new[] { '\\', '/' });
        if (lastSeparator >= 0 && lastSeparator < text.Length - 1)
        {
            var tail = text[(lastSeparator + 1)..].Trim();
            if (tail.Length > 0) text = tail;
        }

        var limit = Math.Max(4, maxChars);
        if (text.Length <= limit) return text;

        // 截断保留开头 + 省略号；预留 1 个字符给省略号，总长不超过 limit
        return text[..(limit - 1)] + "…";
    }

    private static string CollapseWhitespace(string s)
    {
        var span = s.Trim();
        return System.Text.RegularExpressions.Regex.Replace(span, @"\s+", " ");
    }
}
