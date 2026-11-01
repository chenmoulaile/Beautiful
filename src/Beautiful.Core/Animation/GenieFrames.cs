namespace Beautiful.Core.Animation;

/// <summary>
/// 替身帧的实心化数学（纯像素运算，无 WPF 依赖——App 侧 <c>GenieFrameSource</c> 负责位图搬运，
/// 这里只做逐像素判定与合成，公式在单测里锁死）。
/// <para>
/// 【为什么需要】PrintWindow(PW_RENDERFULLCONTENT) 抓回来的帧大片 alpha&lt;255：分层窗口的自绘内容、
/// 浏览器的 GPU 合成面、UWP 的组合面都这样。直接拿去当替身纹理，半透明区域会"看穿"——
/// 用户实测原话"最小化动画的窗口太过透明"。在帧进引擎前统一补齐，三种引擎一次治好。
/// </para>
/// <para>
/// 【合成口径】混到<b>黑底</b>（预乘域标准操作）：out = c·a/255，A 置 255。选黑不选白的理由：
/// 需要"补齐"的像素几乎都是窗口阴影/圆角边缘（本来就暗），混黑保持明度关系；混白会把阴影
/// 提亮成一圈白晕，在深色壁纸上极其扎眼。
/// </para>
/// </summary>
public static class GenieFrames
{
    /// <summary>
    /// 是否存在半透明像素（任何 alpha&lt;255 即 true）。Bgra32 布局：每像素 4 字节，alpha 在第 4 字节。
    /// 全 0 长度按"无半透明"处理（调用方对空帧自有兜底）。
    /// </summary>
    public static bool HasTranslucency(byte[] bgra)
    {
        if (bgra is null) return false;
        for (var i = 3; i < bgra.Length; i += 4)
            if (bgra[i] != 0xFF) return true;
        return false;
    }

    /// <summary>
    /// 就地把整帧合成到黑底（全部像素 A=255；颜色按自身 alpha 预乘）。
    /// <paramref name="width"/>/<paramref name="height"/> 只用于诊断语义（无填充间隙的紧凑 Bgra32 布局
    /// 里像素数 = 长度/4），不参与寻址——步长非紧凑的帧由调用方先拷成紧凑布局再进来。
    /// </summary>
    public static void CompositeOverOpaque(byte[] bgra, int width, int height)
    {
        if (bgra is null) return;
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            if (a == 0xFF) continue;          // 已经实心：原样保留
            if (a == 0)
            {
                bgra[i] = bgra[i + 1] = bgra[i + 2] = 0;   // 全透明 = 黑底本身
                bgra[i + 3] = 0xFF;
                continue;
            }
            bgra[i] = (byte)(bgra[i] * a / 255);
            bgra[i + 1] = (byte)(bgra[i + 1] * a / 255);
            bgra[i + 2] = (byte)(bgra[i + 2] * a / 255);
            bgra[i + 3] = 0xFF;
        }
    }
}
