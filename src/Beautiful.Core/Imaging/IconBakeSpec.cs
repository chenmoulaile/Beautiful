using Beautiful.Core.Themes;

namespace Beautiful.Core.Imaging;

/// <summary>
/// 图标烘焙（一次性烧进位图）的**参数对象**：尺寸/DPI/形状/阴影/高光的算术本体，纯值 + 纯函数，
/// 像素工作留在 App 层（WPF 只能在那边画，但"画多大、画在哪、留多少投影空间"必须能在这里被单测锁死）。
/// <para>
/// 【为什么 tile == 渲染方块的物理像素而不是"256 原图直接喂给 Image"】Dock 的图标是
/// <c>IconSize</c> DIP 的方框：44 DIP 在 100% 是 44 物理 px、150% 是 66、200% 是 88；再叠 macOS 悬停
/// 放大 1.8×。原图只有 48 px 时，这就是"图标看着劣质"的算术根因（v3 T6 实测见 IconExtractor 注释）。
/// 烘焙到位图尺寸恰好等于**当前 DPI 下的渲染方块**，逐帧路径就是 1:1 位图搬运——既不重采样，也不需要
/// 任何 WPF <c>Effect</c>（投影/遮罩/高光都已经烧进像素里）。
/// </para>
/// <para>
/// 【为什么投影要挤在方块内】把投影画在方块之外会让条目盒变大 → 行宽/面板宽跟着变，而 v3 T4 刚把
/// "面板贴图标"的几何交给主题（DockRowMath / StripMath 是禁改区）。所以图块内部的下缘空间就是投影的
/// 全部家当：artwork 相应缩小（<see cref="MinArtworkRatio"/> 兜住下限），位图尺寸不变。
/// </para>
/// </summary>
public sealed record IconBakeSpec
{
    /// <summary>投影纵向偏移占方块边长比例（macOS 的图标投影很贴近，不是大柔光）。</summary>
    public const double ShadowOffsetRatio = 0.045;

    /// <summary>投影模糊半径占方块边长比例（三次盒式模糊的等效 σ，见 <see cref="BoxBlur"/>）。</summary>
    public const double ShadowBlurRatio = 0.06;

    /// <summary>投影不透明度（0..255，#4D ≈ 30%）：暗色面板上再深就成"图标掉在黑洞里"。</summary>
    public const int ShadowOpacity = 0x4D;

    /// <summary>顶部内高光占边长比例（1px 级：太厚就变成塑料边框）。</summary>
    public const double HighlightRatio = 0.06;

    /// <summary>顶部高光强度（#1A 级，规格要求）。</summary>
    public const int HighlightAlpha = 0x1A;

    /// <summary>整圈内描边（玻璃口）强度：比顶部高光更淡，只负责"边缘不糊成一坨"。</summary>
    public const int RimAlpha = 0x14;

    /// <summary>artwork 在图块内的顶部留白比例（其余空间给下缘的投影）。</summary>
    public const double TopSlackRatio = 0.25;

    /// <summary>投影挤占后 artwork 的最小边长占比（低于它宁可把投影做小，也不能把图标缩成点）。</summary>
    public const double MinArtworkRatio = 0.7;

    /// <summary>DPI 合法区间：越界（含 NaN / 手改配置 / 拔屏瞬间的 0）一律回落 1.0，见 <see cref="Scale"/>。</summary>
    public const double MinDpiScale = 0.25, MaxDpiScale = 4.0;

    /// <summary>超采样倍数区间（<see cref="Supersample"/> 落进这里）。</summary>
    public const int MinSupersample = 1, MaxSupersample = 4;

    /// <summary>默认超采样倍数：2× 渲染再一次性降采样（"从 ≥2× 渲染"的最低满足值，再高只是更慢）。</summary>
    public const int DefaultSupersample = 2;

    /// <summary>烘焙位图边长上限（物理像素）：72 DIP·400% = 288，给到 512 是天花板而不是预期用量。</summary>
    public const int MaxTilePx = 512;

    /// <summary>Dock 图标方框边长（DIP，来自主题/设置项 DockIconSize）。</summary>
    public required int IconSizeDip { get; init; }

    /// <summary>本 Dock 所在显示器的缩放（物理/DIP）。</summary>
    public required double DpiScale { get; init; }

    /// <summary>遮罩形状（主题数据）。</summary>
    public IconMaskStyle Mask { get; init; } = IconMaskStyle.None;

    /// <summary>圆角方形半径比例（<see cref="IconMaskStyle.Squircle"/> 忽略本值）。</summary>
    public double MaskRadiusRatio { get; init; }

    /// <summary>是否烘焙投影。</summary>
    public bool Shadow { get; init; }

    /// <summary>是否烘焙顶部内高光 + 内描边。</summary>
    public bool Highlight { get; init; }

    /// <summary>超采样倍数（钳到 [1,4]）。</summary>
    public int Supersample { get; init; } = DefaultSupersample;

    /// <summary>
    /// Dock 悬停放大的最大倍率（<c>MaxScale</c>；不放大时为 1）。
    /// <para>
    /// 【为什么烘焙尺寸必须乘上它】图块过去只按 <c>IconSizeDip·DpiScale</c> 烘，也就是**静止时**的
    /// 大小；而放大到 1.8× 时 WPF 是把这张"基础尺寸"的位图**拉伸**到 1.8 倍画出来——
    /// 用户看到的"放大起来质感很水"就是这个拉伸，不是图标源图不行（源图按 256px 取，分辨率够）。
    /// 按最大倍率烘，放大过程就变成从大图向下采样，这才是清晰的。
    /// </para>
    /// </summary>
    public double Magnification { get; init; } = 1.0;

    /// <summary>合法化的放大倍率：非有限值或 &lt;1 一律按 1（不放大）处理，上限 4。</summary>
    public double MagnificationFactor =>
        double.IsFinite(Magnification) && Magnification > 1.0 ? Math.Min(4.0, Magnification) : 1.0;

    /// <summary>
    /// 合法化后的缩放：NaN/Inf/≤0/超出 [0.25,4] 一律 1.0。
    /// 【为什么不钳到边界值而是回落 1.0】0 会让位图退化成 1px（图标消失），4.5 会把缓存内存放大到
    /// 计划外；1.0 是"看得见的图标 + 可预期的尺寸"，且真实显示器缩放不可能越出 [0.25,4]。
    /// </summary>
    public double Scale =>
        double.IsFinite(DpiScale) && DpiScale >= MinDpiScale && DpiScale <= MaxDpiScale ? DpiScale : 1.0;

    /// <summary>
    /// 烘焙位图边长 = 图块物理像素 = <c>round(IconSizeDip·Scale·MagnificationFactor)</c>，恒 ≥1、
    /// 恒 ≤<see cref="MaxTilePx"/>。乘放大倍率是故意的：见 <see cref="Magnification"/>。
    /// </summary>
    public int TilePx => Math.Clamp(
        (int)Math.Round(Math.Max(1, IconSizeDip) * Scale * MagnificationFactor), 1, MaxTilePx);

    /// <summary>钳制后的**期望**超采样倍数（实际用几倍由 <see cref="SupersampleForSource"/> 定）。</summary>
    public int SupersampleFactor => Math.Clamp(Supersample, MinSupersample, MaxSupersample);

    /// <summary>
    /// 针对某个源图实际该用的超采样倍数：不得超过"源能供给的像素数 / 图块边长"。
    /// 256 的源画到 66 的图块可以走 2×（甚至 3×）；但图块本身已经 288 时再放大到 576 又缩回来，
    /// 只会把已经拿不到的细节抹得更糊 —— 这种退化情形一步到位（1×）反而最锐。
    /// </summary>
    public int SupersampleForSource(int sourcePx)
    {
        if (sourcePx <= 0 || TilePx <= 0) return SupersampleFactor;
        var usable = Math.Max(1, sourcePx / TilePx);
        return Math.Clamp(Math.Min(SupersampleFactor, usable), MinSupersample, MaxSupersample);
    }

    /// <summary>超采样画布边长（物理像素）= 图块 × 实际采用的倍数：先在这里画遮罩/高光/投影，再一次降采样。</summary>
    public int SuperPx(int supersample) => TilePx * Math.Clamp(supersample, MinSupersample, MaxSupersample);

    /// <summary>投影纵向偏移（**图块像素**；超采样画布上要再乘 <see cref="SupersampleFactor"/>）；关投影恒 0。</summary>
    public int ShadowOffsetPx => Shadow ? Math.Max(1, (int)Math.Round(TilePx * ShadowOffsetRatio)) : 0;

    /// <summary>投影模糊半径（图块像素，同上口径）；关投影恒 0。</summary>
    public int ShadowBlurPx => Shadow ? Math.Max(1, (int)Math.Round(TilePx * ShadowBlurRatio)) : 0;

    /// <summary>
    /// artwork 边长 = 图块 - (偏移 + 模糊) —— 即"投影完全装得下"的最大正方形；
    /// 再按 <see cref="MinArtworkRatio"/> 兜底：投影吃得太狠时优先缩小投影空间，而不是把图标缩没。
    /// </summary>
    public int ArtworkPx
    {
        get
        {
            var room = ShadowOffsetPx + ShadowBlurPx;
            var min = Math.Max(1, (int)Math.Ceiling(TilePx * MinArtworkRatio));
            var size = TilePx - room;
            return size < min ? Math.Max(1, min) : size;
        }
    }

    /// <summary>artwork 水平居中后的左缘（超采样与最终像素同比例，App 侧乘自己的坐标因子即可）。</summary>
    public double ArtworkLeftPx => (TilePx - ArtworkPx) / 2.0;

    /// <summary>artwork 顶缘：留白的一部分给顶部（投影主要落在下缘），其余全给下缘。</summary>
    public int ArtworkTopPx => (int)Math.Round(Math.Max(0, TilePx - ArtworkPx) * TopSlackRatio);

    /// <summary>
    /// 投影可用底缘空间 = 图块 - 顶缘 - artwork。**必须 ≥ 偏移**，否则投影被图块下沿硬切
    /// （单测锁这一条：可见尾部被裁是"阴影出现一条直线"的观感来源）。
    /// </summary>
    public int ShadowBottomRoomPx => Math.Max(0, TilePx - ArtworkTopPx - ArtworkPx);

    /// <summary>
    /// 投影是否装得下（下缘空间 ≥ 纵向偏移）。装不下时烘焙**跳过投影**而不是硬画——
    /// 被图块下沿切掉的投影会成一条直线，比没有投影更"劣质"（退化到 1px 级图块时就是这种情形）。
    /// </summary>
    public bool ShadowFits => Shadow && ShadowBottomRoomPx >= ShadowOffsetPx;

    /// <summary>顶部高光厚度（像素，至少 1；关高光恒 0）。</summary>
    public int HighlightPx => Highlight ? Math.Max(1, (int)Math.Round(TilePx * HighlightRatio)) : 0;

    /// <summary>圆角半径（像素，按 artwork 边长；非圆角方形形状恒 0）。</summary>
    public int RadiusPx => IconBake.RadiusPx(Mask, MaskRadiusRatio, ArtworkPx);

    /// <summary>缓存是否等价于"什么都不做"（形状/投影/高光全无 → 只需要降采样）。</summary>
    public bool GeometryOnly => Mask == IconMaskStyle.None && !Shadow && !Highlight;
}

/// <summary>
/// 烘焙位图的缓存键：<b>(图标源, 图标尺寸, DPI, 主题, 处理方式)</b>。
/// <para>
/// 【为什么 DPI 与尺寸都进键而只有像素数不够】同一 DPI 下换主题要换形状；同一像素数下换主题 id
/// 也要重建（主题 id 是形状/投影的来源）。反过来说，**键里没有任何随时间变化的东西**，所以逐帧路径
/// 命中同一键就是同一张冻结位图——这是"Effect-free / 零分配"的成立条件。
/// </para>
/// </summary>
public readonly record struct IconBakeKey
{
    /// <summary>图标源（提取用的路径/命名空间 id）；大小写不敏感（Windows 路径）。</summary>
    public string IconSource { get; init; }

    public int IconSizeDip { get; init; }
    public double DpiScale { get; init; }
    public string ThemeId { get; init; }
    public string Treatment { get; init; }

    /// <summary>
    /// 建键：源/主题/方式全部 trim + 转小写作规范形（"C:\Edge\MsEdge.exe" 与 "c:\edge\msedge.exe"
    /// 必须是同一个键，否则同一次主题切换会把缓存填成两份，LRU 上限就失去意义）。
    /// </summary>
    public static IconBakeKey Create(string? iconSource, IconBakeSpec spec, string? themeId, string? treatment)
        => new()
        {
            IconSource = Normalize(iconSource),
            IconSizeDip = spec.IconSizeDip,
            DpiScale = spec.Scale,   // 用合法化后的值：0/NaN/4.5 这类输入不能各自制造一个键
            ThemeId = Normalize(themeId),
            Treatment = Normalize(treatment)
        };

    private static string Normalize(string? raw) => raw?.Trim().ToLowerInvariant() ?? string.Empty;
}
