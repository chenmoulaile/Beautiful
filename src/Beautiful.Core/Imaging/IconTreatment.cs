using Beautiful.Core.Themes;

namespace Beautiful.Core.Imaging;

/// <summary>
/// 设置项 <c>IconTreatment</c>（"auto" | "mask" | "plain"）→ 实际烘焙参数的解释器。
/// <para>
/// 【形状只有一个属主（v3 T6 裁定）】图标形状要么由**烘焙位图**决定（mask/auto 且有遮罩形状），
/// 要么由 XAML 的 <c>ThemeIconClip</c> 决定（plain，或主题本就无遮罩）。两者同时生效就是"双遮罩"：
/// 同一形状被抗锯齿两遍，边缘多出一圈灰边，而且投影/高光会被后来的 Clip 裁掉。
/// 因此"由烘焙位图拥有形状"为真时 ThemeManager 必须**不注入** ThemeIconClip（见 App 层的
/// <c>IconBakeContext.BakesMask</c>）。
/// </para>
/// <para>
/// <c>auto</c> = 完全跟随主题（macOS 圆角方块 + 投影、鸿蒙 squircle + 投影、GNOME 平铺无投影）；
/// <c>mask</c> = 强制启用主题的遮罩/高光/投影（主题无遮罩时给一个通用圆角，让"想要遮罩"这个意图有反馈）；
/// <c>plain</c> = 只做 2× 超采样降采样到当前 DPI 的像素尺寸，不烧形状、不烧投影、不烧高光
/// （观感等同 v2，但不再糊）。
/// </para>
/// </summary>
public static class IconTreatment
{
    public const string Auto = "auto";
    public const string Mask = "mask";
    public const string Plain = "plain";

    /// <summary>合法取值（设置面板与 <c>AppSettings.Normalized</c> 共用同一份白名单）。</summary>
    public static IReadOnlyList<string> Known { get; } = [Auto, Mask, Plain];

    /// <summary>
    /// 强制"mask"时，主题没给形状就用这个通用圆角比例（Big Sur 图标的观感 ≈ 0.2），
    /// 而不是"用户明明要遮罩却什么也没发生"。
    /// </summary>
    public const double FallbackRadiusRatio = 0.2;

    /// <summary>解析设置串（大小写/首尾空白宽容），未知值按 <see cref="Auto"/>。</summary>
    public static string Canonical(string? raw)
    {
        var value = raw?.Trim().ToLowerInvariant();
        return value switch
        {
            Mask => Mask,
            Plain => Plain,
            _ => Auto
        };
    }

    /// <summary>
    /// 求Effective 参数：遮罩形状 / 半径比例 / 是否投影 / 是否高光 / 是否由烘焙位图拥有形状。
    /// 高光只在真的烧了遮罩时给出——没有形状就没有"形状的内缘"，高光会成一条横在图标上的白条。
    /// </summary>
    public static (IconMaskStyle Mask, double RadiusRatio, bool Shadow, bool Highlight, bool BakesMask) Resolve(
        string? treatment, IconMaskStyle themeMask, double themeRadiusRatio, bool themeShadow)
    {
        switch (Canonical(treatment))
        {
            case Plain:
                // plain：形状交回 XAML 的 ThemeIconClip，位图只有像素，没有形状/投影/高光
                return (IconMaskStyle.None, 0, false, false, false);

            case Mask:
                var forced = themeMask == IconMaskStyle.None ? IconMaskStyle.RoundedRectangle : themeMask;
                var forcedRatio = themeMask == IconMaskStyle.None ? FallbackRadiusRatio : themeRadiusRatio;
                // 强制 mask 时投影也一并给上（用户意图是"要那层浮起来的质感"，不只是裁个角）
                return (forced, forcedRatio, true, true, true);

            default: // auto：主题给什么就是什么
                var bakes = themeMask != IconMaskStyle.None;
                return (themeMask, themeRadiusRatio, themeShadow, bakes, bakes);
        }
    }
}
