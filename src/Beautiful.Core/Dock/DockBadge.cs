using System;

namespace Beautiful.Core.Dock;

/// <summary>
/// Dock 图标角标的纯规则（Phase 7-T2）。
/// <para>
/// macOS 的角标是图标右上角那个红色小胶囊：1–99 直接写数字，超过 99 写 "99+"，0 不显示。
/// 这里把"数字怎么变成字"和"什么情况下不画"钉成纯函数——服务层只负责"数出几条未读"，
/// 不再各自决定截断方式（否则同一个数字在 Dock 与菜单里会写成两种样子）。
/// </para>
/// <para>
/// 负数、NaN 语义：负数按 0（不显示）；调用方传进来的计数不做"猜"，一律由这里钳制。
/// </para>
/// </summary>
public static class DockBadge
{
    /// <summary>角标显示的上限；超过写成 "99+"（macOS 也是三位数就收口）。</summary>
    public const int MaxCount = 99;

    /// <summary>计数 → 角标文字；0/负数返回 null（表示不画角标）。</summary>
    public static string? Format(int count)
    {
        if (count <= 0)
        {
            return null;
        }

        return count > MaxCount ? $"{MaxCount}+" : count.ToString();
    }

    /// <summary>角标是否该画（与 <see cref="Format"/> 同一判定，供 UI 少一次字符串分配）。</summary>
    public static bool IsVisible(int count) => count > 0;

    /// <summary>
    /// 从窗口标题里解析未读数（微信/QQ 这类把未读写在标题里的应用）。
    /// 支持三种常见写法：<c>微信(3)</c>、<c>微信（3）</c>、<c>微信 [3]</c>；
    /// 括号里必须全是数字，且不超过 <see cref="MaxCount"/>+ 的量级（超过 4 位视为标题里的年份等噪声）。
    /// 解析不到返回 0——宁可没有角标，也不要凭空造一个数字。
    /// </summary>
    public static int ParseCountFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return 0;
        }

        // 从右往左找括号：标题里的应用名本身可能带括号，未读数总在最后
        for (var i = title.Length - 1; i >= 1; i--)
        {
            var close = title[i];
            var open = close switch
            {
                ')' => '(',
                '）' => '（',
                ']' => '[',
                '】' => '【',
                _ => '\0'
            };
            if (open == '\0')
            {
                continue;
            }

            var start = title.LastIndexOf(open, i - 1);
            if (start < 0)
            {
                continue;
            }

            var inner = title[(start + 1)..i].Trim();
            if (inner.Length is 0 or > 4)
            {
                continue;
            }

            var allDigits = true;
            foreach (var ch in inner)
            {
                if (!char.IsAsciiDigit(ch))
                {
                    allDigits = false;
                    break;
                }
            }

            if (!allDigits || !int.TryParse(inner, out var count))
            {
                continue;
            }

            return Math.Clamp(count, 0, 9999);
        }

        return 0;
    }
}

/// <summary>
/// Dock 图标进度条的状态（与 Windows <c>TBPF_*</c> 语义对齐，Phase 7-T1 的绘制侧规则）。
/// </summary>
public enum DockProgressState
{
    /// <summary>不显示进度条。</summary>
    None,

    /// <summary>正常进度（跟随强调色/白色）。</summary>
    Normal,

    /// <summary>出错（红）。</summary>
    Error,

    /// <summary>暂停（黄）。</summary>
    Paused,

    /// <summary>不确定进度（来回跑的马灯：值是相位而不是完成度）。</summary>
    Indeterminate
}

/// <summary>
/// Dock 图标进度条的纯规则（Phase 7-T1）。
/// <para>
/// 进度条画在图标下沿一条 3 DIP 的细带上：宽度 = 图标宽 × <see cref="WidthRatio"/>，
/// 填充比例由 <see cref="Clamp"/> 决定。不确定态不显示"填充到哪"，而是整条底色 + 一段来回移动的高亮，
/// 这一段位置由 <see cref="IndeterminatePhase"/> 给出（0..1 循环），App 侧只按它算左缘。
/// </para>
/// </summary>
public static class DockProgressBar
{
    /// <summary>进度条宽度占图标宽度的比例（macOS 约 70%）。</summary>
    public const double WidthRatio = 0.72;

    /// <summary>进度条高度（DIP）。</summary>
    public const double HeightDip = 3.0;

    /// <summary>不确定态高亮段占整条的比例。</summary>
    public const double IndeterminateSegment = 0.32;

    /// <summary>把任意进度钳到 0..1；非有限值按 0（绝不画一条 NaN 宽的条）。</summary>
    public static double Clamp(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            return 0;
        }

        return value >= 1 ? 1 : value;
    }

    /// <summary>进度条左缘偏移（DIP）：图标容器内居中。</summary>
    public static double LeftInset(double iconWidth)
    {
        var width = Math.Max(0, iconWidth) * WidthRatio;
        return Math.Max(0, (iconWidth - width) / 2);
    }

    /// <summary>
    /// 解析拷贝对话框进度条的可访问文本（Explorer 实测给的是 "56%"）。
    /// 解析不到返回 -1（调用方据此画"忙碌但不知道多少"的不确定态），解析到则钳到 0..1。
    /// 允许 "56 %"、"56%"、前后空白；不带百分号的裸数字一律不认——那不是进度语义。
    /// </summary>
    public static double ParsePercentText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return -1;
        }

        var trimmed = text.Trim();
        if (!trimmed.EndsWith('%'))
        {
            return -1;
        }

        var number = trimmed[..^1].Trim().Replace(" ", string.Empty);
        if (number.Length == 0)
        {
            return -1;
        }

        // 只认纯数字（不接受 "1,234" 这类千分位：进度不会到千）
        foreach (var ch in number)
        {
            if (!char.IsAsciiDigit(ch))
            {
                return -1;
            }
        }

        return int.TryParse(number, out var percent) ? Clamp(percent / 100.0) : -1;
    }

    /// <summary>
    /// 不确定态相位（0..1，循环）：<paramref name="elapsedMs"/> 每 <paramref name="periodMs"/> 走一整圈。
    /// 非正值周期按 1 处理，避免除零。
    /// </summary>
    public static double IndeterminatePhase(double elapsedMs, double periodMs = 1400)
    {
        var period = periodMs > 0 ? periodMs : 1;
        if (double.IsNaN(elapsedMs) || elapsedMs <= 0)
        {
            return 0;
        }

        var raw = (elapsedMs % period) / period;
        return raw < 0 ? 0 : raw;
    }
}
