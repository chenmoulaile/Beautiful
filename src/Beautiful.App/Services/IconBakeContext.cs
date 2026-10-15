using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using Beautiful.Core.Imaging;
using Beautiful.Core.Themes;

namespace Beautiful.App.Services;

/// <summary>
/// 烘焙的**环境参数 + 有界缓存**（v3 T6）：主题 / 图标尺寸 / DPI / IconTreatment 四条输入合成一份
/// <see cref="IconBakeSpec"/>，条目构建时按 <see cref="IconBakeKey"/> 取冻结位图。
/// <para>
/// 【缓存上限】64 项 LRU。单项 = TilePx²·4B：典型 44DIP@150% → 66²·4 ≈ 17KB，最坏
/// 72DIP@200% → 144²·4 ≈ 83KB（64 项 ≈ 5.3MB；把 DPI 合法区间放宽到 400% 时 288²·4 ≈ 331KB/项，
/// 上限仍是 64 项 ≈ 21MB，而那种屏下 dock 只会用到其中一小部分）。参数一变就 <c>Clear()</c>，
/// 所以"来回切十次主题"不会累加——每次切换后缓存里只有当前那一组尺寸/主题的条目。
/// </para>
/// <para>
/// 【逐帧路径碰不到这里】<see cref="Apply"/> 只在条目构建（模板实例化 / DataContext 更换）时被调用，
/// 一次字典查找命中即返回已冻结的位图；未命中才烘焙一次。动画循环仍然只写 Left/Top/ScaleTransform。
/// </para>
/// </summary>
internal static class IconBakeContext
{
    /// <summary>缓存条目上限（见类型注释的内存账）。</summary>
    public const int CacheCapacity = 64;

    private static readonly LruCache<IconBakeKey, BitmapSourceHolder> Cache = new(CacheCapacity);

    /// <summary>当前烘焙参数（默认：鸿蒙主题 + 44DIP + 100% + auto）。</summary>
    public static IconBakeSpec Spec { get; private set; } =
        new() { IconSizeDip = 44, DpiScale = 1.0, Mask = IconMaskStyle.Squircle, Shadow = true, Highlight = true };

    /// <summary>当前主题标识（进缓存键）。</summary>
    public static string ThemeId { get; private set; } = "HarmonyOS";

    /// <summary>当前 IconTreatment 设置值（规范形，进缓存键）。</summary>
    public static string Treatment { get; private set; } = IconTreatment.Auto;

    /// <summary>
    /// 形状是否由烘焙位图拥有。<b>true 时 ThemeManager 必须不注入 <c>ThemeIconClip</c></b>，
    /// 否则同一形状被裁两遍（双抗锯齿灰边 + 投影被裁）。这是 T6 的"形状单一属主"不变量。
    /// </summary>
    public static bool BakesMask => Spec.Mask != IconMaskStyle.None;

    /// <summary>缓存项数（诊断/验收用：主题切换后应当回落到"当前可见条目数"）。</summary>
    public static int CachedCount => Cache.Count;

    /// <summary>
    /// 把四条输入落成参数；返回**是否发生变化**（变化时缓存已清空，调用方要重建条目让绑定重新求值）。
    /// </summary>
    /// <param name="theme">当前主题定义（提供 IconMask / IconMaskRadiusRatio / IconShadow）。</param>
    /// <param name="iconSizeDip">Dock 图标方框边长（DIP，来自设置项）。</param>
    /// <param name="dpiScale">本 Dock 所在显示器的缩放（物理/DIP）。</param>
    /// <param name="treatment">设置项 IconTreatment。</param>
    public static bool Configure(ThemeDefinition theme, double iconSizeDip, double dpiScale, string? treatment)
    {
        var (mask, ratio, shadow, highlight, _) = IconTreatment.Resolve(
            treatment, theme.IconMask, theme.IconMaskRadiusRatio, theme.IconShadow);

        var spec = new IconBakeSpec
        {
            IconSizeDip = (int)Math.Round(Math.Max(1, iconSizeDip)),
            DpiScale = dpiScale,
            Mask = mask,
            MaskRadiusRatio = ratio,
            Shadow = shadow,
            Highlight = highlight
        };
        var canonical = IconTreatment.Canonical(treatment);

        var changed = !spec.Equals(Spec) || theme.Id != ThemeId || canonical != Treatment;
        if (!changed) return false;

        Spec = spec;
        ThemeId = theme.Id;
        Treatment = canonical;
        Cache.Clear();   // 参数变了旧位图就没有再命中的可能：立刻让路，而不是留在 LRU 里占上限
        Logger.Info($"图标烘焙参数: 主题={theme.Id} 尺寸={spec.IconSizeDip}DIP dpi={spec.Scale:0.##} " +
                    $"图块={spec.TilePx}px 形状={spec.Mask} 半径={spec.RadiusPx}px " +
                    $"投影={(spec.ShadowFits ? $"{spec.ShadowOffsetPx}px/模糊{spec.ShadowBlurPx}px" : "关")} " +
                    $"高光={spec.HighlightPx}px 方式={canonical} → 缓存已清空");
        return true;
    }

    /// <summary>
    /// 取（或烘）某条目当前参数下的位图。<paramref name="raw"/> 为 null 直接返回 null（视图回退字形）。
    /// 烘焙失败时把原图存进缓存 —— 既回落得干净，又不会每次重建条目都重试一遍坏路径。
    /// </summary>
    /// <param name="iconSource">提取用的源路径（<see cref="Dock.DockItem.IconSource"/>）；为空时用原位图实例
    /// 的引用身份兜底，避免"所有无源条目抢同一个空键"。</param>
    public static BitmapSource? Apply(string? iconSource, BitmapSource? raw)
    {
        if (raw is null) return null;
        var source = string.IsNullOrWhiteSpace(iconSource)
            ? "raw@" + RuntimeHelpers.GetHashCode(raw).ToString(CultureInfo.InvariantCulture)
            : iconSource!;
        var key = IconBakeKey.Create(source, Spec, ThemeId, Treatment);
        if (Cache.TryGet(key, out var hit)) return hit.Value;

        var baked = IconBaker.Bake(raw, Spec) ?? raw;
        Cache.Set(key, new BitmapSourceHolder(baked));
        return baked;
    }

    /// <summary>清空缓存（测试与"图标源整体变了"用；参数变更走 <see cref="Configure"/> 的自动清空）。</summary>
    public static void ClearCache() => Cache.Clear();

    /// <summary>缓存值包装：让 LRU 的 <c>default!</c> 分支不可能被误当成"命中了一张 null 位图"。</summary>
    private readonly record struct BitmapSourceHolder(BitmapSource Value);
}
