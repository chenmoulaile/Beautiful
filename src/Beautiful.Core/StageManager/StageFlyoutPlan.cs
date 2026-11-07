using System;
using Beautiful.Core.Layout;

namespace Beautiful.Core.StageManager;

/// <summary>二级弹层（同应用窗口列）的落位结果（DIP，相对所在屏工作区左上角）。</summary>
/// <param name="LeftDip">弹层左缘（DIP）。</param>
/// <param name="TopDip">弹层上缘（DIP）。</param>
public readonly record struct StageFlyoutPlacement(double LeftDip, double TopDip);

/// <summary>
/// 台前调度二级弹层（同应用多窗列表）的落位纯函数：贴在舞台条展开位右缘、纵向对齐被点击的
/// 卡片，双向钳制进所在屏工作区。全部 DIP 数学收口在这里，App 侧只做 DIP↔物理换算——
/// 与 <see cref="StageSlideMotion"/> 一样可以被单测逐点验证（右缘放不下 → 左移钳制；
/// 底部放不下 → 上移钳制；极端窄屏不跑出屏外）。
/// </summary>
public static class StageFlyoutPlan
{
    /// <summary>弹层与舞台条展开位之间的水平间隙（DIP）。</summary>
    public const double BarGapDip = 10;

    /// <summary>弹层四周与工作区边缘的最小留白（DIP）。</summary>
    public const double EdgeMarginDip = 8;

    /// <summary>
    /// 求"窗口列弹层"落位。<paramref name="work"/> 为所在屏工作区（物理 px），配合
    /// <paramref name="dpiScale"/> 折成 DIP；<paramref name="barRightDip"/> 为舞台条展开态右缘
    /// （= 工作区左缘 + 条宽）；<paramref name="anchorTopDip"/> 为被点击卡片上缘（DIP，相对工作区）。
    /// 横向：优先 条右缘+间隙，右缘放不下则整体左移钳到工作区右留白；纵向：优先对齐卡片顶，
    /// 底部放不下则上移钳到工作区下留白。永不返回屏外坐标。
    /// </summary>
    public static StageFlyoutPlacement Place(
        PhysicalRect work, double dpiScale,
        double barRightDip, double anchorTopDip,
        double flyoutWidthDip, double flyoutHeightDip)
    {
        var scale = dpiScale > 0 ? dpiScale : 1.0;
        var workLeftDip = StripMath.ToDip(work.Left, scale);
        var workTopDip = StripMath.ToDip(work.Top, scale);
        var workRightDip = StripMath.ToDip(work.Right, scale);
        var workBottomDip = StripMath.ToDip(work.Bottom, scale);

        // 横向：理想位 = 条右缘 + 间隙；放不下 → 钳到右留白；屏比弹层还窄 → 贴左留白（绝不屏外）。
        var minLeft = workLeftDip + EdgeMarginDip;
        var maxLeft = Math.Max(minLeft, workRightDip - EdgeMarginDip - flyoutWidthDip);
        var left = Math.Clamp(barRightDip + BarGapDip, minLeft, maxLeft);

        // 纵向：理想位 = 卡片顶；放不下 → 钳到底留白；同理绝不屏外。
        var minTop = workTopDip + EdgeMarginDip;
        var maxTop = Math.Max(minTop, workBottomDip - EdgeMarginDip - flyoutHeightDip);
        var top = Math.Clamp(anchorTopDip, minTop, maxTop);

        return new StageFlyoutPlacement(left, top);
    }
}
