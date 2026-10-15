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
        var clip = CreateIconClip(theme, theme.IconSize);
        if (clip is not null) dict["ThemeIconClip"] = clip; // 键缺省 → DynamicResource 不解析 → Clip=null（macOS 不遮罩）
        ApplyDockMetrics(dict, theme, theme.IconSize);
        // 先移除旧主题字典再合并，避免叠加（旧 clip 几何随旧字典一并清理）
        for (var i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
            if (app.Resources.MergedDictionaries[i].Source?.OriginalString.Contains("/Themes/") == true)
                app.Resources.MergedDictionaries.RemoveAt(i);
        app.Resources.MergedDictionaries.Add(dict);
        ThemeChanged?.Invoke(theme);
    }

    /// <summary>
    /// 注入 Dock 的**度量类**主题键：图标尺寸（设置项 DockIconSize 覆盖主题默认）与分隔符几何。
    /// 分隔符的线粗/两侧留白直接来自 <see cref="ThemeDefinition"/>，高度取"静止行高 × 比例"——
    /// 条目模板只 DynamicResource 引用这些数值键，于是渲染器里既没有硬编码的 1px/35DIP，
    /// 也没有 <c>if (theme == mac)</c>：主题关分隔符时那种条目压根不会被生成（见 DockViewModel）。
    /// </summary>
    private static void ApplyDockMetrics(ResourceDictionary dict, ThemeDefinition theme, double iconSize)
    {
        dict["ThemeIconSize"] = iconSize;
        // 行高按倒影关闭的静止态算（倒影开启时行高再加一截，但 macOS 原生没有倒影，比例基准用原生行高）
        var rowHeight = iconSize + theme.DockRowInsetDip;
        dict["ThemeSeparatorWidthDip"] = theme.DockSeparatorWidthDip;
        dict["ThemeSeparatorHeightDip"] = theme.DockSeparatorHeightRatio * rowHeight;
        dict["ThemeSeparatorBoxWidthDip"] = theme.DockSeparatorBoxWidthDip;
    }

    /// <summary>
    /// 运行时图标尺寸覆盖（设置项 DockIconSize，Task 16）。主题字典把 <c>ThemeIconSize</c> 写成静态值
    /// （各主题 44），条目模板的图片宽高与遮罩几何都 DynamicResource 引用它；这里把当前主题字典里的该键
    /// 覆盖为用户尺寸，并按新尺寸重建遮罩（squircle/圆角方形几何随尺寸缩放），DynamicResource 即刻重解析
    /// → Dock 图标即时变大变小。macOS（ratio=0 无遮罩）不注入 clip 键；若旧字典曾注入则一并移除，避免
    /// 遗留 44×44 的遮罩把放大后的图标裁掉。
    /// </summary>
    public static void ApplyIconSize(double iconSize, Application app)
    {
        var dict = null as ResourceDictionary;
        for (var i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
            if (app.Resources.MergedDictionaries[i].Source?.OriginalString.Contains("/Themes/") == true)
            {
                dict = app.Resources.MergedDictionaries[i];
                break;
            }
        if (dict is null) return; // 主题字典尚未合并（构造早期）：Apply 时已按主题默认尺寸注入，无需覆盖
        ApplyDockMetrics(dict, Current, iconSize);
        var clip = CreateIconClip(Current, iconSize);
        if (clip is not null) dict["ThemeIconClip"] = clip;
        else dict.Remove("ThemeIconClip");
    }

    /// <summary>
    /// 图标遮罩策略（spec 4.2 表）：ratio=0 不遮罩（macOS 原样）；ratio=0.25 鸿蒙 squircle
    /// （Superellipse nExp=4.4，|x/22|^4.4+|y/22|^4.4=1）；其余 = 圆角方形（半径 = ratio·size，Linux 取 8）。
    /// 遮罩几何按传入 size 构建（与图片宽高同源），使尺寸覆盖后遮罩仍严丝合缝。
    /// </summary>
    private static Geometry? CreateIconClip(ThemeDefinition theme, double size)
    {
        if (theme.IconCornerRadiusRatio <= 0) return null;
        if (theme.IconCornerRadiusRatio == 0.25) return CreateSquircleClip(size);
        var radius = theme.IconCornerRadiusRatio * size;
        var rect = new RectangleGeometry(new Rect(0, 0, size, size), radius, radius);
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
