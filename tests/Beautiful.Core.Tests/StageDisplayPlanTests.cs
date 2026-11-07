using System;
using System.Collections.Generic;
using System.Linq;
using Beautiful.Core.Layout;
using Beautiful.Core.StageManager;
using Xunit;

/// <summary>
/// 每屏一条舞台条（wp-macos-parity r6）的两块纯数学：MRU 快照按屏拆分（SplitByMonitor，
/// 归属映射由 App 侧供给——最小化窗口沿用最后归属是调用方的纪律，这里只验划分）与
/// 二级弹层落位（StageFlyoutPlan，双向钳制进工作区、永不屏外）。
/// </summary>
public class StageDisplayPlanTests
{
    private static StageWindow W(int handle, string appId, string appName, string title = "")
        => new(new IntPtr(handle), appId, appName, title, IsMinimized: false);

    // ---------- SplitByMonitor：划分 ----------

    [Fact]
    public void 按归属屏拆分且各份保持MRU序()
    {
        var input = new[]
        {
            W(1, "edge.exe", "Edge"),
            W(2, "notepad.exe", "记事本"),
            W(3, "shell.exe", "终端"),
            W(4, "edge.exe", "Edge", "标签 B"),
        };
        var monitorOf = new Dictionary<IntPtr, IntPtr>
        {
            [new IntPtr(1)] = new(100),
            [new IntPtr(2)] = new(200),
            [new IntPtr(3)] = new(100),
            [new IntPtr(4)] = new(200),
        };

        var split = StageGrouper.SplitByMonitor(input, monitorOf);

        Assert.Equal(2, split.Count);
        Assert.Equal(new[] { 1, 3 }, split[new IntPtr(100)].Select(w => w.Handle.ToInt32()));
        Assert.Equal(new[] { 2, 4 }, split[new IntPtr(200)].Select(w => w.Handle.ToInt32()));
    }

    [Fact]
    public void 重复句柄去重与Build同口径()
    {
        var input = new[] { W(1, "a.exe", "A"), W(1, "a.exe", "A"), W(2, "b.exe", "B") };
        var monitorOf = new Dictionary<IntPtr, IntPtr>
        { [new IntPtr(1)] = new(100), [new IntPtr(2)] = new(100) };

        var split = StageGrouper.SplitByMonitor(input, monitorOf);

        Assert.Single(split[new IntPtr(100)], w => w.Handle == new IntPtr(1));
        Assert.Equal(2, split[new IntPtr(100)].Count);
    }

    [Fact]
    public void 无归属或零屏句柄的窗口不出现在任何份()
    {
        var input = new[]
        {
            W(1, "a.exe", "A"),   // 映射缺失
            W(2, "b.exe", "B"),   // 映射为 Zero
            W(3, "c.exe", "C"),   // 正常
        };
        var monitorOf = new Dictionary<IntPtr, IntPtr>
        { [new IntPtr(2)] = IntPtr.Zero, [new IntPtr(3)] = new(100) };

        var split = StageGrouper.SplitByMonitor(input, monitorOf);

        var screen = Assert.Single(split);
        Assert.Equal(new IntPtr(100), screen.Key);
        Assert.Equal(new[] { 3 }, screen.Value.Select(w => w.Handle.ToInt32()));
    }

    [Fact]
    public void 空输入与空映射产出空结果_null抛异常()
    {
        Assert.Empty(StageGrouper.SplitByMonitor(Array.Empty<StageWindow>(), new Dictionary<IntPtr, IntPtr>()));
        Assert.Throws<ArgumentNullException>(
            () => StageGrouper.SplitByMonitor(null!, new Dictionary<IntPtr, IntPtr>()));
        Assert.Throws<ArgumentNullException>(
            () => StageGrouper.SplitByMonitor(Array.Empty<StageWindow>(), null!));
    }

    [Fact]
    public void 拆分不修改入参序列()
    {
        var input = new List<StageWindow> { W(1, "a.exe", "A"), W(2, "b.exe", "B") };
        var monitorOf = new Dictionary<IntPtr, IntPtr>
        { [new IntPtr(1)] = new(100), [new IntPtr(2)] = new(200) };

        _ = StageGrouper.SplitByMonitor(input, monitorOf);

        Assert.Equal(2, input.Count);   // 纯函数：入参原样
    }

    // ---------- StageFlyoutPlan：落位 ----------

    [Fact]
    public void 理想位在条右缘加间隙_纵向对齐卡片顶()
    {
        var work = new PhysicalRect(0, 0, 1920, 1040);
        var p = StageFlyoutPlan.Place(work, 1.0, barRightDip: 196, anchorTopDip: 100,
            flyoutWidthDip: 236, flyoutHeightDip: 228);

        Assert.Equal(196 + StageFlyoutPlan.BarGapDip, p.LeftDip);
        Assert.Equal(100, p.TopDip);
    }

    [Fact]
    public void 右缘放不下整体左移钳到右留白()
    {
        // 工作区 400 DIP 宽：理想位 206 + 236 = 442 > 400-8，钳到 400-8-236=156。
        var work = new PhysicalRect(0, 0, 400, 1040);
        var p = StageFlyoutPlan.Place(work, 1.0, 196, 100, 236, 228);

        Assert.Equal(156, p.LeftDip);
        Assert.True(p.LeftDip + 236 <= 400, "右缘不得越出工作区");
    }

    [Fact]
    public void 底部放不上移钳到底留白()
    {
        var work = new PhysicalRect(0, 0, 1920, 600);
        var p = StageFlyoutPlan.Place(work, 1.0, 196, 500, 236, 228);

        Assert.Equal(600 - StageFlyoutPlan.EdgeMarginDip - 228, p.TopDip);
    }

    [Fact]
    public void 极窄屏贴左留白_绝不跑出屏外()
    {
        // 240 DIP 宽的屏比弹层+两份留白（236+16=252）还窄：Math.Max 护栏把右钳位顶回左留白，
        // 左缘贴 8（不产生负坐标/屏外）。弹层放得下的更宽屏走"钳到右留白"分支（见上一用例）。
        var work = new PhysicalRect(0, 0, 240, 1040);
        var p = StageFlyoutPlan.Place(work, 1.0, 196, 100, 236, 228);

        Assert.Equal(StageFlyoutPlan.EdgeMarginDip, p.LeftDip);
        Assert.InRange(p.TopDip, StageFlyoutPlan.EdgeMarginDip, 1040 - StageFlyoutPlan.EdgeMarginDip);
    }

    [Fact]
    public void 高DPI屏按物理工作区换算DIP后同一答案()
    {
        // 2× 缩放的 3840×2080 物理工作区 ≡ 1920×1040 DIP：落位答案必须与 1× 完全一致。
        var work = new PhysicalRect(0, 0, 3840, 2080);
        var p = StageFlyoutPlan.Place(work, 2.0, 196, 100, 236, 228);

        Assert.Equal(206, p.LeftDip);
        Assert.Equal(100, p.TopDip);
    }

    [Fact]
    public void 非零原点的工作区坐标不被假设为零()
    {
        // 副屏在主屏右侧（x=1920 起）：落位必须相对该屏自己的工作区原点。
        var work = new PhysicalRect(1920, 0, 3840, 1040);
        var p = StageFlyoutPlan.Place(work, 1.0, 2116, 100, 236, 228);

        Assert.Equal(1920 + 206, p.LeftDip);   // = 屏原点 + 条宽 + 间隙
        Assert.Equal(100, p.TopDip);
    }

    [Fact]
    public void 缩放非法时按1处理()
    {
        var work = new PhysicalRect(0, 0, 1920, 1040);
        var p = StageFlyoutPlan.Place(work, 0, 196, 100, 236, 228);

        Assert.Equal(206, p.LeftDip);   // 0/负 scale 不得把坐标乘爆
    }
}
