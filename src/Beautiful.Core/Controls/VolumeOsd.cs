using System;

namespace Beautiful.Core.Controls;

/// <summary>音量 HUD 上那个喇叭图标该画成哪一档（纯语义，不绑字体码点）。</summary>
public enum VolumeGlyph
{
    /// <summary>静音（喇叭带斜杠）。</summary>
    Mute,

    /// <summary>低音量（无弧线或一道弧）。</summary>
    Low,

    /// <summary>中音量（两道弧）。</summary>
    Medium,

    /// <summary>高音量（三道弧）。</summary>
    High
}

/// <summary>
/// 音量 HUD（Phase 4-T7）的纯算术：按键步进、亮格数、图标档位、淡出透明度。
/// <para>
/// macOS 的音量 HUD 是 16 格，每次按键正好走一格（= 6.25%），而不是 Windows 的 2%。
/// 这里把"步进要吸附到 16 分度"钉成纯函数：否则连按十几次后会出现"格数与真实音量对不上"
/// ——那种偏差在真机上只能靠数格子发现，单测能提前钉死。
/// </para>
/// <para>
/// 边界：百分比一律钳到 0..100；静音时亮格数为 0（但滑杆真值不被改写，取消静音立即回到原格数）；
/// 淡出透明度按经过时间线性递减，超出时长恒为 0，绝不返回负数。
/// </para>
/// </summary>
public static class VolumeOsd
{
    /// <summary>HUD 的格数（macOS 16 格）。</summary>
    public const int Segments = 16;

    /// <summary>一格对应的百分比（6.25）。</summary>
    public const double PercentPerSegment = 100.0 / Segments;

    /// <summary>HUD 完全显示后开始淡出的时间。</summary>
    public static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(900);

    /// <summary>淡出时长。</summary>
    public static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(250);

    /// <summary>把任意百分比钳到 0..100（非有限值按 0）。</summary>
    public static int Clamp(int percent)
    {
        if (percent <= 0) return 0;
        return percent >= 100 ? 100 : percent;
    }

    /// <summary>
    /// 音量键步进：先把当前值吸附到最近的格，再走一格。返回 0..100。
    /// </summary>
    public static int Step(int currentPercent, bool up)
    {
        var clamped = Clamp(currentPercent);
        var segment = (int)Math.Round(clamped / PercentPerSegment, MidpointRounding.AwayFromZero);
        segment = Math.Clamp(segment + (up ? 1 : -1), 0, Segments);
        return (int)Math.Round(segment * PercentPerSegment, MidpointRounding.AwayFromZero);
    }

    /// <summary>亮着的格数：静音为 0；否则按百分比向上取整（1% 也点亮第一格，符合"能听见就有一格"）。</summary>
    public static int LitSegments(int percent, bool muted)
    {
        if (muted)
        {
            return 0;
        }

        var clamped = Clamp(percent);
        if (clamped == 0)
        {
            return 0;
        }

        return Math.Clamp((int)Math.Ceiling(clamped / PercentPerSegment), 0, Segments);
    }

    /// <summary>喇叭图标档位：按百分比四档；静音优先。</summary>
    public static VolumeGlyph GlyphFor(int percent, bool muted)
    {
        if (muted)
        {
            return VolumeGlyph.Mute;
        }

        var clamped = Clamp(percent);
        return clamped switch
        {
            <= 0 => VolumeGlyph.Mute,
            <= 33 => VolumeGlyph.Low,
            <= 66 => VolumeGlyph.Medium,
            _ => VolumeGlyph.High
        };
    }

    /// <summary>给状态栏/日志用的一行读数（静音时写明"静音"，不显示 0% 冒充）。</summary>
    public static string Describe(int percent, bool muted)
        => muted ? "静音" : $"{Clamp(percent)}%";

    /// <summary>按经过时间算透明度：先恒 1，再线性淡出到 0。</summary>
    public static double OpacityAfter(TimeSpan elapsed, TimeSpan? hold = null, TimeSpan? fade = null)
    {
        var holdFor = hold ?? HoldDuration;
        var fadeFor = fade ?? FadeDuration;
        if (elapsed <= holdFor)
        {
            return 1.0;
        }

        if (fadeFor <= TimeSpan.Zero)
        {
            return 0.0;
        }

        var over = elapsed - holdFor;
        if (over >= fadeFor)
        {
            return 0.0;
        }

        return 1.0 - (over.TotalMilliseconds / fadeFor.TotalMilliseconds);
    }

    /// <summary>总显示时长（保持 + 淡出）。</summary>
    public static TimeSpan TotalDuration(TimeSpan? hold = null, TimeSpan? fade = null)
        => (hold ?? HoldDuration) + (fade ?? FadeDuration);
}
