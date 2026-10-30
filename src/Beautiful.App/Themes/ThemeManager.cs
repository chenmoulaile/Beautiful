using System.Windows;
using System.Windows.Media;
using Beautiful.App.Services;
using Beautiful.Core.Design;
using Beautiful.Core.Themes;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;   // UseWindowsForms 隐式 using 与 System.Drawing.Color 冲突
using Point = System.Windows.Point;

namespace Beautiful.App.Themes;

/// <summary>
/// 外观引擎入口（v4 Phase 1 T4）：Apply = 换 Current 记录 + 清理旧 /Themes/*.xaml 字典后合并新字典
/// + 按当前外观模式注入 <see cref="MacTokens"/>（亮/暗两套颜色/度量/字体/动效）+ 注入运行时几何
/// （图标遮罩几何，冻结以提升渲染性能）+ 广播 ThemeChanged。
/// 换外观 = 重新写同一份字典的 token 键（DynamicResource 即时生效），不重建窗口。
/// </summary>
public static class ThemeManager
{
    public static ThemeDefinition Current { get; private set; } = ThemeDefinition.MacOs();
    public static event Action<ThemeDefinition>? ThemeChanged;

    private static string _appearance = AppearanceModes.Dark;
    private static double _iconSize = 44;

    /// <summary>当前生效的外观模式（dark/light/auto 的规范化值）。</summary>
    public static string Appearance => _appearance;

    /// <summary>当前实际是否深色（auto 时取系统 AppsUseLightTheme）。</summary>
    public static bool IsDark => AppearanceModes.IsDark(_appearance, SystemAppearance.IsLight);

    public static void Apply(ThemeDefinition theme, Application app)
    {
        Current = theme;
        _appearance = AppearanceModes.Canonical(SettingsService.Current.Appearance);
        var uri = new Uri($"/Themes/{theme.Id}.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };
        var clip = CreateIconClip(theme, theme.IconSize);
        if (clip is not null) dict["ThemeIconClip"] = clip; // 键缺省 → DynamicResource 不解析 → Clip=null（macOS 不遮罩）
        ApplyTokens(dict, theme, theme.IconSize);
        // 先移除旧主题字典再合并，避免叠加（旧 clip 几何随旧字典一并清理）
        for (var i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
            if (app.Resources.MergedDictionaries[i].Source?.OriginalString.Contains("/Themes/") == true)
                app.Resources.MergedDictionaries.RemoveAt(i);
        app.Resources.MergedDictionaries.Add(dict);
        ThemeChanged?.Invoke(theme);
    }

    /// <summary>
    /// 外观模式变更（设置页/系统亮暗广播）：只重写 token 键，不重载 XAML、不重建窗口。
    /// 颜色/字体/圆角都是 DynamicResource → 所有已打开表面即时响应。
    /// </summary>
    public static void ApplyAppearance(string? appearance, Application app)
    {
        _appearance = AppearanceModes.Canonical(appearance);
        var dict = FindThemeDictionary(app);
        if (dict is null) return; // 主题字典尚未合并（构造早期）：Apply 时会按当时的模式贴一次
        ApplyTokens(dict, Current, _iconSize);
        Logger.Info($"外观切换：{_appearance}（实际 {(_appearance == AppearanceModes.Auto ? (IsDark ? "深色" : "浅色") : _appearance)}）");
    }

    private static void ApplyTokens(ResourceDictionary dict, ThemeDefinition theme, double iconSize)
    {
        var accent = MacAccents.Hex(SettingsService.Current.AccentColor, IsDark);
        MacTokenDictionary.Apply(dict, MacTokens.For(IsDark), accent, FontProbe.Current.Family);
        ApplyDockMetrics(dict, theme, iconSize);
    }

    private static ResourceDictionary? FindThemeDictionary(Application app)
    {
        for (var i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
            if (app.Resources.MergedDictionaries[i].Source?.OriginalString.Contains("/Themes/") == true)
                return app.Resources.MergedDictionaries[i];
        return null;
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
        _iconSize = iconSize;
        var dict = FindThemeDictionary(app);
        if (dict is null) return; // 主题字典尚未合并（构造早期）：Apply 时已按主题默认尺寸注入，无需覆盖
        ApplyDockMetrics(dict, Current, iconSize);
        var clip = CreateIconClip(Current, iconSize);
        if (clip is not null) dict["ThemeIconClip"] = clip;
        else dict.Remove("ThemeIconClip");
    }

    /// <summary>
    /// 图标遮罩策略（spec 4.2 表）：ratio=0 不遮罩（macOS 原样）；其余 = 圆角方形（半径 = ratio·size）。
    /// 遮罩几何按传入 size 构建（与图片宽高同源），使尺寸覆盖后遮罩仍严丝合缝。
    /// <para>
    /// 【形状只有一个属主（v3 T6）】烘焙位图自己已经把遮罩/高光/投影烧进像素时（<see cref="Services.IconBakeContext.BakesMask"/>），
    /// 这里必须返回 null → 模板的 <c>Clip={DynamicResource ThemeIconClip}</c> 解析不到键 → Clip=null，
    /// 避免"同一形状被抗锯齿两遍"的灰边及投影被 XAML clip 切掉。
    /// </para>
    /// </summary>
    private static Geometry? CreateIconClip(ThemeDefinition theme, double size)
    {
        if (Services.IconBakeContext.BakesMask) return null;
        if (theme.IconCornerRadiusRatio <= 0) return null;
        var radius = theme.IconCornerRadiusRatio * size;
        var rect = new RectangleGeometry(new Rect(0, 0, size, size), radius, radius);
        rect.Freeze();
        return rect;
    }
}
