using System.Windows;
using System.Windows.Media;
using Beautiful.Core.Themes;
using Application = System.Windows.Application;
using Point = System.Windows.Point;

namespace Beautiful.App.Themes;

/// <summary>
/// 主题引擎入口：Apply = 换 Current 记录 + 清理旧 /Themes/*.xaml 字典后合并新字典
/// + 注入运行时几何（鸿蒙 squircle 图标遮罩，冻结以提升渲染性能）+ 广播 ThemeChanged。
/// DockWindow 订阅 ThemeChanged 重设亚克力/放大参数；其余外观（底色/圆角/图标尺寸/字体/指示）
/// 经 DynamicResource 引用字典键，换字典即即时生效，无需逐控件改写。
/// </summary>
public static class ThemeManager
{
    public static ThemeDefinition Current { get; private set; } = ThemeDefinition.HarmonyOs();
    public static event Action<ThemeDefinition>? ThemeChanged;

    public static void Apply(ThemeDefinition theme, Application app)
    {
        Current = theme;
        var uri = new Uri($"/Themes/{theme.Id}.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };
        var clip = CreateIconClip(theme);
        if (clip is not null) dict["ThemeIconClip"] = clip; // 键缺省 → DynamicResource 不解析 → Clip=null（macOS 不遮罩）
        // 先移除旧主题字典再合并，避免叠加（旧 clip 几何随旧字典一并清理）
        for (var i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
            if (app.Resources.MergedDictionaries[i].Source?.OriginalString.Contains("/Themes/") == true)
                app.Resources.MergedDictionaries.RemoveAt(i);
        app.Resources.MergedDictionaries.Add(dict);
        ThemeChanged?.Invoke(theme);
    }

    /// <summary>
    /// 图标遮罩策略（spec 4.2 表）：ratio=0 不遮罩（macOS 原样）；ratio=0.25 鸿蒙 squircle
    /// （Superellipse nExp=4.4，|x/22|^4.4+|y/22|^4.4=1）；其余 = 圆角方形（半径 = ratio·IconSize，Linux 取 8）。
    /// </summary>
    private static Geometry? CreateIconClip(ThemeDefinition theme)
    {
        if (theme.IconCornerRadiusRatio <= 0) return null;
        if (theme.IconCornerRadiusRatio == 0.25) return CreateSquircleClip(theme.IconSize);
        var radius = theme.IconCornerRadiusRatio * theme.IconSize;
        var rect = new RectangleGeometry(new Rect(0, 0, theme.IconSize, theme.IconSize), radius, radius);
        rect.Freeze();
        return rect;
    }

    /// <summary>Superellipse 采样 → 平移到 (0,0)-(size,size) 后构建冻结 StreamGeometry（96 段折线，视觉平滑）。</summary>
    private static Geometry CreateSquircleClip(double iconSize)
    {
        var half = iconSize / 2;
        var pts = Superellipse.Points(half, half, nExp: 4.4);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(pts[0].X + half, pts[0].Y + half), isFilled: true, isClosed: true);
            for (var i = 1; i < pts.Count; i++)
                ctx.LineTo(new Point(pts[i].X + half, pts[i].Y + half), isStroked: true, isSmoothJoin: false);
        }
        geo.Freeze();
        return geo;
    }
}
