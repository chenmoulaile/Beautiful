namespace Beautiful.Core.Imaging;

/// <summary>一个不透明 RGB 颜色（取色结果的载体；Hex 形式给日志与设置回显用）。</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    /// <summary><c>#RRGGBB</c>（大写十六进制，与 <see cref="Themes.ThemeDefinition.AccentHex"/> 同一形式）。</summary>
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// 从 BGRA 像素缓冲取"强调色"（v3 T7；壁纸取色功能已在 v4 Phase 1 删除，
/// 纯函数保留给 Phase 5 Launchpad 壁纸处理/暗遮罩取样）。
/// <para>
/// 【算法三步，每步都为"稳"服务】
/// ① <b>降采样</b>：按最长边抽稀到 ≤<see cref="MaxSamples"/> 个探针像素（跳过透明像素）。壁纸是
///   4K 图时这里就是全部成本上限——O(采样数)，与文件像素数无关，且绝不分配大缓冲。
/// ② <b>量化</b>：每通道截到 5 位（32³ 个格子）后统计"每格的像素数 + 通道和"。这是 k-means 的廉价替身：
///   格内取平均即质心，一次遍历完成，没有迭代、没有随机初值，因此<b>同一输入恒得同一输出</b>
///   （取色结果会写进界面，抖动比"不够聪明"更不可接受）。
/// ③ <b>选格</b>：打分 <c>像素数 × (0.25 + 饱和度)</c> 取最高。只看"最常见"会被大面积天空/暗背景主导，
///   只看"最鲜艳"会被单个荧光噪点主导——这个乘积正是"既常见又有色相"的那一簇。
/// </para>
/// <para>
/// 【为什么不返回"看起来像"的颜色】饱和度低于 <see cref="MinSaturation"/>（黑白灰壁纸、纯色桌面）或
/// 数据不足（空/1px）时<b>返回 false</b>，由调用方回落主题强调色并记录原因。把灰色当强调色画出去，
/// 在深色 Dock 上就是"脏黑块"——按本仓库裁定（没有能力支撑就不画），宁可不取。
/// </para>
/// <para>
/// 【可读性钳制】选中簇的均值若太暗（V&lt;<see cref="MinValue"/>）会被抬到该下限，色相与饱和度不变。
/// 这一步是为了"选中项高亮在深色玻璃卡片上仍看得见"，而不是猜用户想要什么颜色。
/// </para>
/// </summary>
public static class DominantColor
{
    /// <summary>抽稀后的探针像素边长上限（96×96 已足够定色调，再多只是变慢）。</summary>
    public const int MaxSamples = 96;

    /// <summary>低于此饱和度即视为"无色相的灰/白/黑壁纸"，不取色（返回 false 让调用方回落）。</summary>
    public const double MinSaturation = 0.12;

    /// <summary>强调色的最低明度：低于则抬高（保证高亮在深色背板上不糊成一团黑）。</summary>
    public const double MinValue = 0.34;

    /// <summary>参与取色的最小像素数（含）：0×0/1×1/2×2 这类输入信息量不足，直接判不可取。</summary>
    public const int MinArea = 4;

    /// <summary>
    /// 取一帧 BGRA（每像素 4 字节，B,G,R,A 顺序，与 WPF 的 <c>PixelFormats.Bgra32</c> 一致）的强调色。
    /// </summary>
    /// <param name="bgra">像素缓冲，长度至少 <c>width*height*4</c>（多出部分忽略，便于调用方复用缓冲）。</param>
    /// <param name="width">行宽（像素）。</param>
    /// <param name="height">行数（像素）。</param>
    /// <param name="accent">成功时为取到的颜色；失败时为 <c>default</c>（调用方不得画它，见返回值语义）。</param>
    /// <returns>false = 数据不足 / 全透明 / 无有效色相（调用方回落主题强调色）。</returns>
    public static bool TryExtract(ReadOnlySpan<byte> bgra, int width, int height, out RgbColor accent)
    {
        accent = default;
        if (width <= 0 || height <= 0 || width * (long)height < MinArea) return false;
        if (bgra.Length < width * (long)height * 4) return false;

        // ① 抽稀 + ② 量化：一次遍历完成。32³ 桶用一维数组（索引 = r*1024 + g*32 + b），
        //    count 兼作"桶是否被用过"的标记，省去 HashSet。
        var step = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)MaxSamples));
        var count = new int[32 * 32 * 32];
        var sumR = new int[32 * 32 * 32];
        var sumG = new int[32 * 32 * 32];
        var sumB = new int[32 * 32 * 32];
        var sampled = 0;

        for (var y = 0; y < height; y += step)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x += step)
            {
                var i = row + x * 4;
                if (bgra[i + 3] < 128) continue;   // 透明像素不参与（图标/带 alpha 的 PNG 壁纸）
                var b = bgra[i];
                var g = bgra[i + 1];
                var r = bgra[i + 2];
                var bucket = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                count[bucket]++;
                sumR[bucket] += r;
                sumG[bucket] += g;
                sumB[bucket] += b;
                sampled++;
            }
        }
        if (sampled < MinArea) return false;

        // ③ 选格：分数 = 像素数 × (0.25 + 饱和度)。并列时取<b>先出现的桶</b>（索引小者），
        //    于是同一帧的取色结果与遍历顺序无关且稳定（严格 &gt;，后面的相等不覆盖）。
        var bestIndex = -1;
        double bestScore = 0;
        double bestSaturation = 0;
        for (var bucket = 0; bucket < count.Length; bucket++)
        {
            if (count[bucket] == 0) continue;
            var avgR = sumR[bucket] / (double)count[bucket];
            var avgG = sumG[bucket] / (double)count[bucket];
            var avgB = sumB[bucket] / (double)count[bucket];
            var max = Math.Max(avgR, Math.Max(avgG, avgB));
            var min = Math.Min(avgR, Math.Min(avgG, avgB));
            var saturation = max <= 0 ? 0 : (max - min) / max;
            var score = count[bucket] * (0.25 + saturation);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = bucket;
                bestSaturation = saturation;
            }
        }
        if (bestIndex < 0) return false;

        var r0 = (byte)Math.Round(sumR[bestIndex] / (double)count[bestIndex]);
        var g0 = (byte)Math.Round(sumG[bestIndex] / (double)count[bestIndex]);
        var b0 = (byte)Math.Round(sumB[bestIndex] / (double)count[bestIndex]);

        // 无色相（灰阶壁纸）：不取。理由见类型注释——画出去的灰块比"没取到"更难看了。
        if (bestSaturation < MinSaturation) return false;

        // 明度钳制（色相/饱和度不动）：太暗的簇抬到下限，避免高亮在深色玻璃上不可见。
        ToHsv(r0, g0, b0, out var h, out var s, out var v);
        if (v < MinValue)
        {
            accent = FromHsv(h, s, MinValue);
            return true;
        }
        accent = new RgbColor(r0, g0, b0);
        return true;
    }

    // ---------- HSV（只在钳制那一步用到，因此只在这里出现；纯函数便于单测间接覆盖） ----------

    private static void ToHsv(byte r, byte g, byte b, out double hue, out double saturation, out double value)
    {
        var rf = r / 255.0;
        var gf = g / 255.0;
        var bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var delta = max - min;
        value = max;
        saturation = max <= 0 ? 0 : delta / max;
        if (delta <= 0) { hue = 0; return; }           // 灰：色相无定义，给 0（上面已按饱和度拦下）
        if (max == rf) hue = ((gf - bf) / delta + (gf < bf ? 6 : 0)) / 6;
        else if (max == gf) hue = ((bf - rf) / delta + 2) / 6;
        else hue = ((rf - gf) / delta + 4) / 6;
    }

    private static RgbColor FromHsv(double hue, double saturation, double value)
    {
        var i = (int)Math.Floor(hue * 6);
        var f = hue * 6 - i;
        var p = value * (1 - saturation);
        var q = value * (1 - f * saturation);
        var t = value * (1 - (1 - f) * saturation);
        var (r, g, b) = (i % 6) switch
        {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q),
        };
        return new RgbColor((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }
}
