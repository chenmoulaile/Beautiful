namespace Beautiful.Core.Imaging;

/// <summary>
/// 墙纸取色管线的一次完整结果（极致美化①）：<see cref="Primary"/>/<see cref="Secondary"/> 是
/// k-means 聚出的前两簇均值，<see cref="Tint"/> 是喂给玻璃公式前派生好的混入色，
/// <see cref="TintHex"/> 是 <c>#AARRGGBB</c>（alpha 携带用户强度），可直接写进
/// <c>AuroraTokens.WallpaperTint</c> / <c>MacTokens.WallpaperTint</c>。
/// <para>
/// 【失败语义与 DominantColor 一致】TryCompute 返回 false 时 result 为 default（Alpha=0），
/// 调用方必须走"撤回 tint、玻璃回主题原生色"，绝不能拿默认值去画。
/// </para>
/// </summary>
public readonly record struct WallpaperTintResult(
    RgbColor Primary,
    RgbColor Secondary,
    RgbColor Tint,
    byte Alpha,
    string TintHex)
{
    /// <summary>Alpha &gt; 0 才代表提色成功；0 = 关闭/撤回（写进 token 也是全透明 = 公式不混）。</summary>
    public bool IsValid => Alpha > 0;
}

/// <summary>
/// 墙纸自适应取色管线（v5 极致美化①）。输入一帧 BGRA 像素（与 WPF <c>PixelFormats.Bgra32</c>
/// 同序），输出一个玻璃混入 tint。全部纯函数：同一输入恒得同一输出——tint 会写进 Dock 玻璃，
/// 抖动比"不够聪明"更不可接受（与 <see cref="DominantColor"/> 同一条仓库裁定）。
/// <para>
/// 【四步管线，每步都为"稳与快"服务】
/// ① <b>降采样</b>：最长边钳到 <see cref="MaxEdge"/>（64px，≤4096 个样本），框平均抗锯齿；
///   4K 壁纸也只有 O(源像素) 一次遍历，总成本上限不受文件分辨率摆布。
/// ② <b>k-means 聚类</b>（k=<see cref="ClusterCount"/>，≤<see cref="MaxIterations"/> 轮）：种子从
///   32³ 量化直方图按"像素数 × (0.25 + 饱和度)"确定性选出（无随机初值），空簇拿离质心最远的
///   样本重置——迭代收敛但轨迹完全确定。比 <see cref="DominantColor"/> 的"量化桶即质心"多花
///   几次遍历，换来的主/辅两色是真实聚类的中心，派生 tint 的色相更贴壁纸观感。
/// ③ <b>派生 tint</b>：色相取主色（辅色与主色色相差 ≥<see cref="MinSecondaryHueDelta"/> 度时向
///   辅色偏移最多 <see cref="MaxHueBlendDegrees"/> 度），饱和度在玻璃基色之上被壁纸往上顶
///   （<see cref="SaturationLift"/> 系数，钳 <see cref="MaxTintSaturation"/>），<b>明度直接贴玻璃
///   基色</b>——混入后玻璃的深浅结构不变，只变色相/饱和度，这正是"调玻璃底色相/饱和度"的字面义。
/// ④ <b>强度入 alpha</b>：0..100 线性映射到 0..255；玻璃公式那侧权重 = alpha × 0.35，
///   满强度时混入权重封顶 35%（玻璃仍是玻璃，不会被壁纸刷成色块）。
/// </para>
/// <para>
/// 【为什么饱和度闸比 DominantColor 更紧】DominantColor 取的是"强调色"（画在图标上，点睛），
/// 这里取的是"大面积混色"（铺进整条 Dock 玻璃，铺底）——<see cref="MinWallpaperSaturation"/>
/// 0.15 &gt; DominantColor 的 0.12：更没把握的灰调壁纸宁可不动玻璃，也不给用户一条脏色 Dock。
/// </para>
/// <para>
/// 【饱和度的两套口径，各司其职】"有没有彩度"的判定（闸与簇打分）用 HSV 的 S（与
/// <see cref="DominantColor"/> 同公式同哲学）；"派生成什么颜色"的变换用 HSL（明度语义与
/// 玻璃基色对齐）。只读不写的地方用 HSV，颜色生成只走 HSL。
/// </para>
/// </summary>
public static class WallpaperTint
{
    /// <summary>降采样后的最长边上限（64×64 = 4096 样本已足够定色调，再多只是变慢）。</summary>
    public const int MaxEdge = 64;

    /// <summary>k-means 簇数：主色之外还要留一个"辅色"候选，再多对 tint 派生没有增量。</summary>
    public const int ClusterCount = 4;

    /// <summary>Lloyd 迭代上限。壁纸色调聚 4 簇时通常 2-3 轮就收敛，8 是防病态输入的护栏。</summary>
    public const int MaxIterations = 8;

    /// <summary>参与取色的最小像素数（含）：与 <see cref="DominantColor.MinArea"/> 同口径。</summary>
    public const int MinArea = 4;

    /// <summary>主色低于此 HSV 饱和度即视为灰阶壁纸，不取色（返回 false 让调用方撤 tint）。</summary>
    public const double MinWallpaperSaturation = 0.15;

    /// <summary>派生 tint 的饱和度下限：再低混进玻璃等于没混（效果不明显的混色不如不混）。</summary>
    public const double MinTintSaturation = 0.10;

    /// <summary>派生 tint 的饱和度上限：玻璃仍是玻璃，不许被荧光壁纸刷成色块。</summary>
    public const double MaxTintSaturation = 0.45;

    /// <summary>壁纸饱和度往玻璃基色饱和度上顶的系数：sat = glass + wallpaper × 本系数。</summary>
    public const double SaturationLift = 0.35;

    /// <summary>辅色参与主色色相的份额（色相差 × 本系数），封顶见 <see cref="MaxHueBlendDegrees"/>。</summary>
    public const double SecondaryHueShare = 0.30;

    /// <summary>辅色最多把主色相拉动的度数：辅色只提供"色彩倾向"，不夺主色的主导权。</summary>
    public const double MaxHueBlendDegrees = 40;

    /// <summary>辅色与主色色相至少差这么多度才参与混色相；近色相的辅色混进来只是噪声。</summary>
    public const double MinSecondaryHueDelta = 30;

    /// <summary>种子挑选时桶与已有种子的最小 RGB 欧氏距离：保证初始簇是"真的不同色"。</summary>
    public const double SeedMinDistance = 48;

    /// <summary>强度滑杆默认值（开箱即可见但不夸张：alpha=153 → 玻璃混入权重 ≈ 0.21）。</summary>
    public const int DefaultStrength = 60;

    /// <summary>强度滑杆上限（0..100 线性映射到 tint alpha 0..255）。</summary>
    public const int MaxStrength = 100;

    /// <summary>玻璃公式里 tint 混入权重的系数（与 AuroraTokenDictionary.DockGlassGradient 的
    /// <c>tint.ScA * 0.35</c> 同源；App 侧 macOS 三停混合也用它，两边权重永远一个数）。</summary>
    public const double GlassMixWeight = 0.35;

    /// <summary>
    /// 完整管线：降采样（需要时）→ k-means 取主/辅色 → 派生 tint → 强度入 alpha。
    /// </summary>
    /// <param name="bgra">像素缓冲，长度至少 <c>width*height*4</c>（多出部分忽略）。</param>
    /// <param name="width">行宽（像素）。</param>
    /// <param name="height">行数（像素）。</param>
    /// <param name="glassBase">当前主题的玻璃基色（如 Aurora 的 <c>GlassBase</c> token）；
    /// tint 的明度贴着它走，换主题重派生即可换底。</param>
    /// <param name="strength">用户强度 0..100（越界值先钳制，与设置规范化同口径）。</param>
    /// <param name="result">成功时为结果；失败时为 default（调用方必须撤 tint，不得画默认值）。</param>
    /// <returns>false = 输入不足 / 全透明 / 灰阶壁纸（主色无彩度）。</returns>
    public static bool TryCompute(ReadOnlySpan<byte> bgra, int width, int height,
        RgbColor glassBase, int strength, out WallpaperTintResult result)
    {
        result = default;
        if (width <= 0 || height <= 0 || width * (long)height < MinArea) return false;
        if (bgra.Length < width * (long)height * 4) return false;

        ReadOnlySpan<byte> samples;
        int sampleW, sampleH;
        if (width <= MaxEdge && height <= MaxEdge)
        {
            samples = bgra;
            sampleW = width;
            sampleH = height;
        }
        else
        {
            var (dw, dh) = DownsampledSize(width, height);
            var down = new byte[dw * dh * 4];
            Downsample(bgra, width, height, down, dw, dh);
            samples = down;
            sampleW = dw;
            sampleH = dh;
        }

        if (!ExtractPalette(samples, sampleW, sampleH, out var primary, out var secondary)) return false;
        ToHsv(primary, out _, out var primarySaturation, out _);
        if (primarySaturation < MinWallpaperSaturation) return false;   // 灰阶壁纸：不取（见类型注释）

        var tint = DeriveTint(glassBase, primary, secondary);
        var alpha = (byte)Math.Round(ClampStrength(strength) / (double)MaxStrength * 255);
        result = new WallpaperTintResult(primary, secondary, tint, alpha,
            $"#{alpha:X2}{tint.R:X2}{tint.G:X2}{tint.B:X2}");
        return true;
    }

    /// <summary>强度钳制（设置滑杆与手改 JSON 共用一条口径）。</summary>
    public static int ClampStrength(int strength) => Math.Clamp(strength, 0, MaxStrength);

    // ══════════════════════════ ① 降采样 ══════════════════════════

    /// <summary>
    /// 降采样目标尺寸：最长边钳到 <paramref name="maxEdge"/>、短边按比例，向上取整到至少 1px；
    /// 源本身不超限时原样返回（不放大）。
    /// </summary>
    public static (int Width, int Height) DownsampledSize(int width, int height, int maxEdge = MaxEdge)
    {
        var longest = Math.Max(width, height);
        if (longest <= maxEdge) return (width, height);
        var dw = Math.Max(1, (int)Math.Round(width * (double)maxEdge / longest));
        var dh = Math.Max(1, (int)Math.Round(height * (double)maxEdge / longest));
        return (dw, dh);
    }

    /// <summary>
    /// 框平均降采样：每个目标像素 = 源上对应矩形块内<b>不透明</b>像素（A≥128）的 B/G/R 通道均值。
    /// 块内没有不透明像素 → 输出 A=0（提色阶段按透明跳过）。透明像素的 RGB 不参与平均——
    /// 带透明通道的 PNG 壁纸若把透明块的任意 RGB 混进来，均值就是编造的颜色。
    /// </summary>
    public static void Downsample(ReadOnlySpan<byte> source, int srcW, int srcH,
        Span<byte> destination, int dstW, int dstH)
    {
        for (var dy = 0; dy < dstH; dy++)
        {
            var y0 = dy * srcH / dstH;
            var y1 = Math.Max(y0 + 1, (dy + 1) * srcH / dstH);
            for (var dx = 0; dx < dstW; dx++)
            {
                var x0 = dx * srcW / dstW;
                var x1 = Math.Max(x0 + 1, (dx + 1) * srcW / dstW);
                int sumB = 0, sumG = 0, sumR = 0, used = 0;
                for (var y = y0; y < y1; y++)
                {
                    var row = y * srcW * 4;
                    for (var x = x0; x < x1; x++)
                    {
                        var i = row + x * 4;
                        if (source[i + 3] < 128) continue;
                        sumB += source[i];
                        sumG += source[i + 1];
                        sumR += source[i + 2];
                        used++;
                    }
                }

                var o = (dy * dstW + dx) * 4;
                if (used > 0)
                {
                    destination[o] = (byte)(sumB / used);
                    destination[o + 1] = (byte)(sumG / used);
                    destination[o + 2] = (byte)(sumR / used);
                    destination[o + 3] = 255;
                }
                else
                {
                    destination[o + 3] = 0;
                }
            }
        }
    }

    // ══════════════════════════ ② k-means 聚类 ══════════════════════════

    /// <summary>
    /// k-means 提前两簇：<see cref="Primary"/> = 打分最高簇（常见 × 有彩度），<see cref="Secondary"/>
    /// = 次高簇（只有一个有效簇时与主色相同，派生阶段自会跳过辅色混色）。
    /// <para>确定性三保障：种子按（分数降序、桶号升序）取；样本分配并列取簇号小；簇打分并列取
    /// 簇号小——同一帧输入无论跑多少遍，输出逐字节相同。</para>
    /// </summary>
    internal static bool ExtractPalette(ReadOnlySpan<byte> bgra, int width, int height,
        out RgbColor primary, out RgbColor secondary)
    {
        primary = default;
        secondary = default;
        var total = width * height;
        if (total < MinArea) return false;

        // ① 32³ 量化直方图（与 DominantColor 同一张网）：只用于挑种子，桶均值即初始质心。
        var count = new int[32 * 32 * 32];
        var sumR = new int[32 * 32 * 32];
        var sumG = new int[32 * 32 * 32];
        var sumB = new int[32 * 32 * 32];
        var sampled = 0;
        for (var i = 0; i < total; i++)
        {
            var o = i * 4;
            if (bgra[o + 3] < 128) continue;   // 透明像素不参与（与 DominantColor 同口径）
            var b = bgra[o];
            var g = bgra[o + 1];
            var r = bgra[o + 2];
            var bucket = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
            count[bucket]++;
            sumR[bucket] += r;
            sumG[bucket] += g;
            sumB[bucket] += b;
            sampled++;
        }

        if (sampled < MinArea) return false;

        // ② 种子挑选：活跃桶按（分数降序、桶号升序）排列，依次收"与已有种子距离足够远"的桶。
        var scored = new List<(int Bucket, double Score)>();
        for (var bucket = 0; bucket < count.Length; bucket++)
        {
            if (count[bucket] == 0) continue;
            var r = sumR[bucket] / (double)count[bucket];
            var g = sumG[bucket] / (double)count[bucket];
            var b = sumB[bucket] / (double)count[bucket];
            scored.Add((bucket, count[bucket] * (0.25 + HsvSaturation(r, g, b))));
        }

        scored.Sort((a, c) =>
        {
            var byScore = c.Score.CompareTo(a.Score);      // 分数降序
            return byScore != 0 ? byScore : a.Bucket.CompareTo(c.Bucket);   // 并列取桶号小（全序确定）
        });

        var centroids = new List<(double R, double G, double B)>();
        CollectSeeds(scored, sumR, sumG, sumB, count, requireDistance: true, centroids);
        if (centroids.Count < ClusterCount)
            CollectSeeds(scored, sumR, sumG, sumB, count, requireDistance: false, centroids);

        // ③ 样本数组（供 Lloyd 迭代复用；一次填充，迭代期零分配）。
        var pixels = new byte[sampled * 3];
        var index = 0;
        for (var i = 0; i < total; i++)
        {
            var o = i * 4;
            if (bgra[o + 3] < 128) continue;
            pixels[index] = bgra[o + 2];       // R
            pixels[index + 1] = bgra[o + 1];   // G
            pixels[index + 2] = bgra[o];       // B
            index += 3;
        }

        // ④ Lloyd 迭代：分配（平方欧氏距离，并列取簇号小）→ 均值更新 → 空簇拿最远样本重置。
        var k = centroids.Count;
        var assign = new int[sampled];
        Array.Fill(assign, -1);                // -1 保证首轮必然产生"变化"，不会被误判收敛
        var counts = new int[k];
        var sums = new double[k * 3];
        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            Array.Clear(counts);
            Array.Clear(sums);
            var changed = false;
            for (var s = 0; s < sampled; s++)
            {
                var pr = pixels[s * 3];
                var pg = pixels[s * 3 + 1];
                var pb = pixels[s * 3 + 2];
                var best = 0;
                var bestDist = double.MaxValue;
                for (var j = 0; j < k; j++)
                {
                    var dr = pr - centroids[j].R;
                    var dg = pg - centroids[j].G;
                    var db = pb - centroids[j].B;
                    var d = dr * dr + dg * dg + db * db;
                    if (d < bestDist)          // 严格 <：并列取簇号小 → 与遍历顺序无关
                    {
                        bestDist = d;
                        best = j;
                    }
                }

                if (assign[s] != best)
                {
                    assign[s] = best;
                    changed = true;
                }

                counts[best]++;
                sums[best * 3] += pr;
                sums[best * 3 + 1] += pg;
                sums[best * 3 + 2] += pb;
            }

            for (var j = 0; j < k; j++)
            {
                if (counts[j] > 0)
                {
                    centroids[j] = (sums[j * 3] / counts[j], sums[j * 3 + 1] / counts[j], sums[j * 3 + 2] / counts[j]);
                }
                else
                {
                    // 空簇（种子重叠时可能发生）：拿离自己质心最远的样本重置。并列取样本序号小，
                    // 保持确定性；若所有样本都在原地（单色图），均值不变，下一轮自然收敛。
                    var farthest = 0;
                    var farthestDist = -1.0;
                    for (var s = 0; s < sampled; s++)
                    {
                        var pr = pixels[s * 3];
                        var pg = pixels[s * 3 + 1];
                        var pb = pixels[s * 3 + 2];
                        var dr = pr - centroids[j].R;
                        var dg = pg - centroids[j].G;
                        var db = pb - centroids[j].B;
                        var d = dr * dr + dg * dg + db * db;
                        if (d > farthestDist)
                        {
                            farthestDist = d;
                            farthest = s;
                        }
                    }

                    centroids[j] = (pixels[farthest * 3], pixels[farthest * 3 + 1], pixels[farthest * 3 + 2]);
                }
            }

            if (!changed) break;   // 分配稳定即收敛（首轮 assign=-1 必变化，不会误触发）
        }

        // ⑤ 簇打分（与 DominantColor 选格同公式：像素数 × (0.25+饱和度)），取前两簇。
        var clusters = new List<(int Index, double Score, double R, double G, double B)>();
        for (var j = 0; j < k; j++)
        {
            if (counts[j] == 0) continue;
            clusters.Add((j, counts[j] * (0.25 + HsvSaturation(centroids[j].R, centroids[j].G, centroids[j].B)),
                centroids[j].R, centroids[j].G, centroids[j].B));
        }

        if (clusters.Count == 0) return false;
        clusters.Sort((a, c) =>
        {
            var byScore = c.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : a.Index.CompareTo(c.Index);   // 并列取簇号小（全序确定）
        });

        primary = FromClusterMean(clusters[0]);
        secondary = clusters.Count > 1 ? FromClusterMean(clusters[1]) : primary;
        return true;
    }

    /// <summary>按序收种子：requireDistance=true 时只收与已有种子距离 ≥ <see cref="SeedMinDistance"/>
    /// 的桶（首桶无条件收）；false 时放宽距离把数量补足到 <see cref="ClusterCount"/>。</summary>
    private static void CollectSeeds(List<(int Bucket, double Score)> scored, int[] sumR, int[] sumG, int[] sumB,
        int[] count, bool requireDistance, List<(double R, double G, double B)> centroids)
    {
        foreach (var (bucket, _) in scored)
        {
            if (centroids.Count >= ClusterCount) return;
            var candidate = (sumR[bucket] / (double)count[bucket], sumG[bucket] / (double)count[bucket], sumB[bucket] / (double)count[bucket]);
            if (centroids.Count > 0 && requireDistance)
            {
                var farEnough = true;
                foreach (var existing in centroids)
                {
                    var dr = existing.Item1 - candidate.Item1;
                    var dg = existing.Item2 - candidate.Item2;
                    var db = existing.Item3 - candidate.Item3;
                    if (Math.Sqrt(dr * dr + dg * dg + db * db) >= SeedMinDistance) continue;
                    farEnough = false;
                    break;
                }

                if (!farEnough) continue;
            }

            if (!centroids.Contains(candidate)) centroids.Add(candidate);
        }
    }

    private static RgbColor FromClusterMean((int Index, double Score, double R, double G, double B) cluster) => new(
        (byte)Math.Round(cluster.R),
        (byte)Math.Round(cluster.G),
        (byte)Math.Round(cluster.B));

    // ══════════════════════════ ③ tint 派生（HSL 调玻璃底色相/饱和度） ══════════════════════════

    /// <summary>
    /// 从主/辅色派生玻璃 tint：色相 = 主色（辅色色相差足够大时向辅色偏移 ≤40°）；
    /// 饱和度 = 玻璃基色饱和度 + 壁纸主色饱和度 × <see cref="SaturationLift"/>（钳
    /// [<see cref="MinTintSaturation"/>, <see cref="MaxTintSaturation"/>]）；
    /// <b>明度 = 玻璃基色明度</b>（混入后玻璃的深浅结构不变）。
    /// </summary>
    public static RgbColor DeriveTint(RgbColor glassBase, RgbColor primary, RgbColor secondary)
    {
        var (primaryHue, primarySaturation, _) = ToHsl(primary);
        var hue = primaryHue;
        if (secondary != primary)
        {
            var (secondaryHue, secondarySaturation, _) = ToHsl(secondary);
            if (secondarySaturation >= MinWallpaperSaturation)
            {
                var delta = HueDelta(primaryHue, secondaryHue);   // 最短方向，(-180, 180]
                if (Math.Abs(delta) >= MinSecondaryHueDelta)
                {
                    var shift = Math.Clamp(delta * SecondaryHueShare, -MaxHueBlendDegrees, MaxHueBlendDegrees);
                    hue = primaryHue + shift;
                }
            }
        }

        var (_, glassSaturation, glassLightness) = ToHsl(glassBase);
        var saturation = Math.Clamp(glassSaturation + primarySaturation * SaturationLift,
            MinTintSaturation, MaxTintSaturation);
        return FromHsl(hue, saturation, glassLightness);
    }

    /// <summary>色相最短方向差（to − from，归一化到 (−180, 180]）。</summary>
    public static double HueDelta(double from, double to)
    {
        var d = (to - from) % 360;
        if (d > 180) d -= 360;
        if (d <= -180) d += 360;
        return d;
    }

    // ══════════════════════════ ④ hex 工具（App 侧装配用，Core 无 WPF 依赖） ══════════════════════════

    /// <summary>
    /// 解析 <c>#RRGGBB</c> / <c>#AARRGGBB</c>（大小写均可）；缺 alpha 按 255。
    /// 失败返回 false（App 侧回落全透明，绝不因一个颜色字符串崩主题）。
    /// </summary>
    public static bool TryParseHex(string? hex, out byte r, out byte g, out byte b, out byte a)
    {
        r = g = b = a = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var span = hex.Trim();
        if (span[0] != '#') return false;
        if (span.Length == 9)
        {
            if (!TryParseByte(span[1], span[2], out a) || !TryParseByte(span[3], span[4], out r)
                || !TryParseByte(span[5], span[6], out g) || !TryParseByte(span[7], span[8], out b))
                return false;
            return true;
        }

        if (span.Length == 7
            && TryParseByte(span[1], span[2], out r)
            && TryParseByte(span[3], span[4], out g)
            && TryParseByte(span[5], span[6], out b))
        {
            a = 255;
            return true;
        }

        return false;
    }

    /// <summary>两位十六进制 → byte；任一位非法返回 false。</summary>
    private static bool TryParseByte(char high, char low, out byte value)
    {
        var h = HexValue(high);
        var l = HexValue(low);
        if (h >= 0 && l >= 0)
        {
            value = (byte)((h << 4) | l);
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>单个十六进制位的数值；非法字符返回 -1。</summary>
    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>
    /// 把 tint 混进基色（RGB 逐通道线性，<paramref name="weight"/> 是 tint 的权重）；
    /// <b>基色的 alpha 原样保留</b>（tint 的 alpha 不参与——权重由调用方显式传），基色是
    /// <c>#AARRGGBB</c> 就回 <c>#AARRGGBB</c>，是 <c>#RRGGBB</c> 就回 <c>#RRGGBB</c>。
    /// weight=0 恒等（macOS 三停混合在关态必须逐字节等于静态字典值）；基色/tint 解析失败时返回
    /// 基色原样（宁可不动玻璃，不画编造色）。
    /// </summary>
    public static string MixHex(string baseHex, string tintHex, double weight)
    {
        if (!TryParseHex(baseHex, out var br, out var bg, out var bb, out var ba)) return baseHex;
        if (!TryParseHex(tintHex, out var tr, out var tg, out var tb, out _)) return baseHex;
        var t = Math.Clamp(weight, 0, 1);
        var rr = (byte)Math.Round(br + (tr - br) * t);
        var rg = (byte)Math.Round(bg + (tg - bg) * t);
        var rb = (byte)Math.Round(bb + (tb - bb) * t);
        return ba == 255
            ? $"#{rr:X2}{rg:X2}{rb:X2}"
            : $"#{ba:X2}{rr:X2}{rg:X2}{rb:X2}";
    }

    // ══════════════════════════ 颜色数学（HSV 只读判彩度；HSL 负责生成） ══════════════════════════

    /// <summary>HSV 饱和度 = (max−min)/max（与 DominantColor 的饱和度同公式；全灰 → 0）。</summary>
    private static double HsvSaturation(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        return max <= 0 ? 0 : (max - min) / max;
    }

    private static void ToHsv(RgbColor c, out double hue, out double saturation, out double value)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        value = Math.Max(r, Math.Max(g, b));
        saturation = HsvSaturation(r, g, b);
        var max = value;
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        if (delta <= 0)
        {
            hue = 0;   // 灰：色相无定义，给 0（饱和度闸已把灰拦下）
            return;
        }

        if (max == r) hue = ((g - b) / delta + (g < b ? 6 : 0)) / 6 * 360;
        else if (max == g) hue = ((b - r) / delta + 2) / 6 * 360;
        else hue = ((r - g) / delta + 4) / 6 * 360;
    }

    /// <summary>HSL（h 0..360，s/l 0..1）——公式与 AuroraTokenDictionary.ToHsl 同源，App 侧两侧派生一致。</summary>
    internal static (double H, double S, double L) ToHsl(RgbColor c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2.0;
        var d = max - min;
        if (d <= 0) return (0, 0, l);
        var s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
        double h;
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return (h * 60.0, s, l);
    }

    private static RgbColor FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t switch
            {
                < 1.0 / 6.0 => p + (q - p) * 6 * t,
                < 1.0 / 2.0 => q,
                < 2.0 / 3.0 => p + (q - p) * (2.0 / 3.0 - t) * 6,
                _ => p,
            };
        }

        if (s <= 0)
        {
            var gray = (byte)Math.Round(l * 255);
            return new RgbColor(gray, gray, gray);
        }

        var q2 = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p2 = 2 * l - q2;
        return new RgbColor(
            (byte)Math.Round(HueToRgb(p2, q2, h / 360 + 1.0 / 3.0) * 255),
            (byte)Math.Round(HueToRgb(p2, q2, h / 360) * 255),
            (byte)Math.Round(HueToRgb(p2, q2, h / 360 - 1.0 / 3.0) * 255));
    }
}
