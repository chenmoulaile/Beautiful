using System;

namespace Beautiful.Core.Finder;

/// <summary>
/// Quick Look 多选翻页的纯算术（Phase 6-b）。
/// <para>
/// macOS 的行为：多选后空格预览，←/→ 在同批文件里循环翻页（到头回到另一端）。
/// 只算索引，不碰文件系统——真正读文件由 App 侧的预览窗口负责。
/// </para>
/// </summary>
public static class QuickLookPager
{
    /// <summary>
    /// 在 <paramref name="count"/> 项里从 <paramref name="index"/> 走 <paramref name="delta"/> 步，越界循环。
    /// </summary>
    /// <param name="index">当前索引（越界会被钳进合法区间）。</param>
    /// <param name="count">总项数；小于等于 0 返回 -1。</param>
    /// <param name="delta">+1 下一项 / -1 上一项。</param>
    /// <returns>新索引；没有可翻的项返回 -1。</returns>
    public static int Step(int index, int count, int delta)
    {
        if (count <= 0)
        {
            return -1;
        }

        if (count == 1)
        {
            return 0;
        }

        var start = Math.Clamp(index, 0, count - 1);
        var next = (start + delta) % count;
        if (next < 0)
        {
            next += count;
        }

        return next;
    }

    /// <summary>是否有多项可翻页（单项/空集时为 false，UI 据此隐藏"3 / 12"计数）。</summary>
    public static bool CanPage(int count) => count > 1;
}
