namespace Beautiful.Core.Layout;

/// <summary>屏幕四角（以显示器矩形的物理像素为准，副屏负坐标同样成立）。</summary>
public enum ScreenCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

/// <summary>
/// 屏幕圆角（对标 MyDockFinder ScreenRound）的纯几何：每屏四角各一块 <c>r×r</c> 物理像素
/// 正方形遮罩区，遮罩 = 正方形减去内切四分之一圆。App 侧只做显示器枚举与窗口落地；
/// "角在哪、半径多大、超出屏幕怎么钳"全部在这里，可脱离 UI 单测。
/// <para>
/// 半径口径：<b>设置存 DIP</b>（与 DockEdgeMargin 同一口径，配置文件里不随 DPI 漂移），
/// 落地时按所在显示器的有效缩放换算成物理像素——同一块屏换分辨率/缩放后观感一致。
/// </para>
/// </summary>
public static class ScreenCornerPlan
{
    /// <summary>遮罩半径下限（物理 px）：小于 1px 的弧画不出来，低于 4px 圆角肉眼不可辨。</summary>
    public const int MinRadiusPx = 4;

    /// <summary>遮罩半径上限（物理 px）：再大遮罩就盖进屏幕内容区了（macOS 真机圆角 ~10-16px）。</summary>
    public const int MaxRadiusPx = 96;

    /// <summary>
    /// 遮罩默认颜色（不透明纯黑）：macOS 真机的屏幕圆角处就是熄屏的显示边，观感即纯黑。
    /// 颜色可配（<c>AppSettings.ScreenCornerMaskColor</c>），但默认不取壁纸/主题色——
    /// 浅色壁纸上深色遮罩才像"屏幕自己的圆角"，跟着主题翻色反而违和（取法说明见该设置项注释）。
    /// </summary>
    public const string DefaultMaskColor = "#FF000000";

    /// <summary>
    /// 遮罩色的<b>预设条目</b>（alpha.29 残项清零，设置页下拉与 settingsContract 同源）：
    /// 名称 + 规范化 hex。全部是熄屏边色系的深色（alpha 恒 FF）——遮罩要像"屏幕自己的圆角"，
    /// 预设不随主题翻色（理由同 <see cref="DefaultMaskColor"/>：浅色主题下圆角翻白会穿帮）；
    /// 色值取自 r4 收尾记录的 settingsContract 建议（纯黑/深灰/炭黑）。
    /// </summary>
    public static IReadOnlyList<(string Name, string Hex)> PresetMaskColors { get; } =
    [
        ("纯黑（默认）", "#FF000000"),
        ("深灰", "#FF1E1E1E"),
        ("炭黑", "#FF262626"),
    ];

    /// <summary>设置半径（DIP）→ 本屏物理像素半径：按该屏有效缩放换算，钳到 [MinRadiusPx, MaxRadiusPx]。</summary>
    public static int RadiusPx(int radiusDip, double dpiScale)
    {
        var scale = dpiScale > 0 ? dpiScale : 1.0;
        var px = (int)Math.Round(radiusDip * scale, MidpointRounding.AwayFromZero);
        return Math.Clamp(px, MinRadiusPx, MaxRadiusPx);
    }

    /// <summary>
    /// 某屏某角的遮罩正方形（物理像素）。半径先按"不超过该屏短边一半"钳制——
    /// 竖屏/超小屏上半径大于半屏时若不钳，四块遮罩会互相重叠（重叠区双重描边还相互穿透）。
    /// 显示器退化（宽或高 ≤0）返回空矩形，调用方跳过该屏。
    /// </summary>
    public static PhysicalRect CornerSquare(PhysicalRect monitor, ScreenCorner corner, int radiusPx)
    {
        if (monitor.Width <= 0 || monitor.Height <= 0) return default;
        // Max(1, …)：1px 宽的退化屏上半径上限是 0，Math.Clamp(v, 1, 0) 会直接抛参数异常
        var maxRadius = Math.Max(1, Math.Min(monitor.Width, monitor.Height) / 2);
        var radius = Math.Clamp(radiusPx, 1, maxRadius);
        return corner switch
        {
            ScreenCorner.TopLeft => new PhysicalRect(monitor.Left, monitor.Top, monitor.Left + radius, monitor.Top + radius),
            ScreenCorner.TopRight => new PhysicalRect(monitor.Right - radius, monitor.Top, monitor.Right, monitor.Top + radius),
            ScreenCorner.BottomLeft => new PhysicalRect(monitor.Left, monitor.Bottom - radius, monitor.Left + radius, monitor.Bottom),
            _ => new PhysicalRect(monitor.Right - radius, monitor.Bottom - radius, monitor.Right, monitor.Bottom)
        };
    }

    /// <summary>遮罩四分之一圆的圆心（该角"朝屏幕内侧"的那个顶点，物理像素）。</summary>
    public static (int X, int Y) ArcCenter(PhysicalRect square, ScreenCorner corner) => corner switch
    {
        ScreenCorner.TopLeft => (square.Right, square.Bottom),
        ScreenCorner.TopRight => (square.Left, square.Bottom),
        ScreenCorner.BottomLeft => (square.Right, square.Top),
        _ => (square.Left, square.Top)
    };

    // ---------- 遮罩颜色（可配，默认纯黑；解析纯 BCL，不依赖 WPF——Core 是 net8.0 无 UI 框架） ----------

    /// <summary>
    /// 解析遮罩颜色字符串："#RRGGBB" / "#AARRGGBB"（'#' 可省、容忍首尾空白）→ 0xAARRGGBB；
    /// 缺 alpha 按 0xFF（不透明）补。格式非法 / 空白 / null 返回 false。
    /// </summary>
    public static bool TryParseMaskColor(string? color, out uint argb)
    {
        argb = 0;
        if (string.IsNullOrWhiteSpace(color)) return false;
        var text = color.Trim();
        if (text[0] == '#') text = text[1..];
        if (text.Length is not (6 or 8)) return false;
        foreach (var ch in text)
            if (!Uri.IsHexDigit(ch)) return false;
        var value = Convert.ToUInt32(text, 16);
        argb = text.Length == 6 ? 0xFF000000u | value : value;
        return true;
    }

    /// <summary>
    /// 遮罩颜色规范化（设置收敛口径，与 <c>AppSettings.Normalized</c> 其它键同一先例）：
    /// 合法值收敛为固定格式 "#AARRGGBB"（6/8 位、带不带 '#'、有无 alpha 都落到同一写法），
    /// 非法值一律回落 <see cref="DefaultMaskColor"/>——手写 settings.json 拼错颜色只丢配色，不丢功能。
    /// </summary>
    public static string CanonicalMaskColor(string? color)
        => TryParseMaskColor(color, out var argb) ? $"#{argb:X8}" : DefaultMaskColor;
}
