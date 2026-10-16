namespace Beautiful.Core.Imaging;

/// <summary>
/// 判断一次"窗口画面抓取"是否真的抓到了内容。
/// <para>
/// <c>PrintWindow</c> 对用 DirectComposition / GPU 表面绘制的窗口经常返回<b>整幅纯色</b>
/// （全黑或全白）。拿这种图去做最小化动画，用户看到的是一块凭空出现的黑卡——比没有动画更糟。
/// 所以动画前必须先过这一关：<b>整幅均匀 = 抓失败 = 不做动画</b>。
/// </para>
/// <para>
/// 判据刻意只看"取样点是否全都一样"，不去猜"多暗算黑"：真实窗口几乎不可能在 5×4 个取样点上
/// 一个字节都不差，而纯色抓取必然是。阈值型判据会把深色主题的正常窗口误杀。
/// </para>
/// </summary>
public static class CaptureUsability
{
    /// <summary>
    /// 取样点是否足以认定"抓到了真实画面"。少于 2 个点无法判断，一律算不可用（宁可不动画）。
    /// </summary>
    public static bool IsUsable(IReadOnlyList<uint> sampledArgb)
    {
        if (sampledArgb is null || sampledArgb.Count < 2) return false;
        var first = sampledArgb[0] & 0xFFFFFF;   // 丢掉 alpha 位：抓取都是不透明的
        for (var i = 1; i < sampledArgb.Count; i++)
            if ((sampledArgb[i] & 0xFFFFFF) != first) return true;
        return false;   // 全部相同 ⇒ 纯色 ⇒ 这次抓取没内容
    }

    /// <summary>
    /// 按网格算出取样点像素坐标（含首尾，避免只采到中间一块）。
    /// 单独拆出来是因为"网格对不对"和"均匀不均匀"是两个容易各自出错的地方。
    /// </summary>
    public static List<(int X, int Y)> SampleGrid(int width, int height, int columns = 5, int rows = 4)
    {
        var points = new List<(int, int)>();
        if (width <= 0 || height <= 0) return points;
        var c = Math.Max(1, columns);
        var r = Math.Max(1, rows);
        for (var y = 0; y < r; y++)
        {
            for (var x = 0; x < c; x++)
            {
                // 落在格子的中心而不是边缘：边缘常是 1px 的窗口边框/圆角缺口
                var px = Math.Min(width - 1, (int)((x + 0.5) * width / c));
                var py = Math.Min(height - 1, (int)((y + 0.5) * height / r));
                points.Add((px, py));
            }
        }
        return points;
    }
}
