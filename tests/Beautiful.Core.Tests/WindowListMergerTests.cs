using System;
using System.Collections.Generic;
using System.Linq;
using Beautiful.Core.Switcher;
using Xunit;

/// <summary>
/// 切换器 MRU 分组与步进数学（Task 10，纯函数）。
/// 约定：入参序列已是"Z 序 + MRU"（顶层在前），GroupByApp 不重排，只做归并；
/// NextIndex 负责循环步进（含负数/越界索引）。
/// </summary>
public class WindowListMergerTests
{
    private static WindowEntry E(int handle, string appId, string appName, string title = "", int priority = 0)
        => new(new IntPtr(handle), appId, appName, title, priority);

    // ---------- GroupByApp ----------

    // 分组保序：不同 App 按首次出现顺序排列，即便同 App 的后续窗口穿插出现也不后移。
    [Fact]
    public void 分组保持首次出现顺序()
    {
        var input = new[]
        {
            E(1, "a.exe", "A"),
            E(2, "b.exe", "B"),
            E(3, "c.exe", "C"),
        };
        var apps = WindowListMerger.GroupByApp(input);
        Assert.Equal(new[] { "a.exe", "b.exe", "c.exe" }, apps.Select(x => x.AppId));
    }

    // 同 App 多窗归并：归入同一 SwitcherApp，窗口保持入参 Z 序；App 位置取首现位置。
    [Fact]
    public void 同应用多窗口归并且窗口保持Z序()
    {
        var input = new[]
        {
            E(10, "edge.exe", "Edge", "第一个标签"),
            E(20, "notepad.exe", "记事本", "a.txt"),
            E(30, "edge.exe", "Edge", "第二个标签"),
            E(40, "edge.exe", "Edge", "第三个标签"),
        };
        var apps = WindowListMerger.GroupByApp(input);

        Assert.Equal(2, apps.Count);
        Assert.Equal("edge.exe", apps[0].AppId);
        Assert.Equal(new[] { 10, 30, 40 }, apps[0].Windows.Select(w => w.Handle.ToInt32()));
        Assert.Equal("notepad.exe", apps[1].AppId);
        Assert.Single(apps[1].Windows);
    }

    // 分组名取该 App 首次出现项的 AppName（后续窗口标题/名称不同也不覆盖）。
    [Fact]
    public void 应用名取首次出现项()
    {
        var input = new[]
        {
            E(1, "a.exe", "首名", "t1"),
            E(2, "a.exe", "别名", "t2"),
        };
        var apps = WindowListMerger.GroupByApp(input);
        Assert.Equal("首名", apps[0].AppName);
        Assert.Equal(2, apps[0].Windows.Count);
    }

    // 空输入 → 空列表（不抛异常）。
    [Fact]
    public void 空输入返回空列表()
    {
        Assert.Empty(WindowListMerger.GroupByApp(Array.Empty<WindowEntry>()));
        Assert.Empty(WindowListMerger.GroupByApp(new List<WindowEntry>()));
    }

    // 单窗口 → 单 App 单窗口。
    [Fact]
    public void 单窗口归为一个应用()
    {
        var apps = WindowListMerger.GroupByApp(new[] { E(7, "solo.exe", "Solo", "标题", 3) });
        var app = Assert.Single(apps);
        Assert.Equal("solo.exe", app.AppId);
        var win = Assert.Single(app.Windows);
        Assert.Equal(new IntPtr(7), win.Handle);
        Assert.Equal(3, win.Priority);
        Assert.Equal("标题", win.Title);
    }

    // 重复句柄去重：同一 hwnd 只保留首次出现（防御 EnumWindows/事件重报造成的重复项）。
    [Fact]
    public void 重复句柄只保留首次()
    {
        var input = new[]
        {
            E(1, "a.exe", "A", "t"),
            E(1, "a.exe", "A", "t"),
            E(2, "b.exe", "B", "t"),
        };
        var apps = WindowListMerger.GroupByApp(input);
        Assert.Equal(2, apps.Count);
        Assert.Single(apps[0].Windows);
    }

    // AppId 大小写不敏感归并（exe 路径在 Windows 上不区分大小写）。
    [Fact]
    public void 应用标识大小写不敏感()
    {
        var input = new[]
        {
            E(1, "Edge.exe", "Edge"),
            E(2, "edge.EXE", "Edge"),
        };
        var apps = WindowListMerger.GroupByApp(input);
        Assert.Single(apps);
        Assert.Equal(2, apps[0].Windows.Count);
    }

    // null 入参抛 ArgumentNullException（调用方须自行保证枚举结果非 null）。
    [Fact]
    public void 空引用抛参数异常()
        => Assert.Throws<ArgumentNullException>(
            () => WindowListMerger.GroupByApp(null!)); // xunit 分析器要求显式 null!

    // ---------- ApplyMinimizedPenalty ----------

    // 全部窗口最小化的 App 排到末尾，其余相对顺序不变（MRU 语义仍生效）。
    [Fact]
    public void 最小化惩罚把最小化应用排后()
    {
        var input = new[]
        {
            E(1, "min.exe", "Min"),
            E(2, "ok.exe", "Ok"),
            E(3, "min.exe", "Min"),
        };
        var apps = WindowListMerger.GroupByApp(input);
        var reordered = WindowListMerger.ApplyMinimizedPenalty(apps, h => h.ToInt32() is 1 or 3);

        Assert.Equal(new[] { "ok.exe", "min.exe" }, reordered.Select(x => x.AppId));
        // 原列表不被就地修改（返回新列表）
        Assert.Equal(new[] { "min.exe", "ok.exe" }, apps.Select(x => x.AppId));
    }

    // 部分窗口未最小化的 App 不受惩罚（仍按 MRU 位置）。
    [Fact]
    public void 仅全部最小化才排后()
    {
        var input = new[]
        {
            E(1, "half.exe", "Half"),
            E(2, "b.exe", "B"),
            E(3, "half.exe", "Half"),
        };
        var apps = WindowListMerger.GroupByApp(input);
        var reordered = WindowListMerger.ApplyMinimizedPenalty(apps, h => h.ToInt32() == 1);
        Assert.Equal(new[] { "half.exe", "b.exe" }, reordered.Select(x => x.AppId));
    }

    [Fact]
    public void 最小化惩罚空列表返回空()
        => Assert.Empty(WindowListMerger.ApplyMinimizedPenalty(new List<SwitcherApp>(), _ => false));

    // ---------- NextIndex ----------

    // 前进一步。
    [Theory]
    [InlineData(0, 3, 1)]
    [InlineData(1, 3, 2)]
    // 末尾回绕到 0
    [InlineData(2, 3, 0)]
    [InlineData(0, 1, 0)]
    public void 向前步进并回绕(int current, int count, int expected)
        => Assert.Equal(expected, WindowListMerger.NextIndex(current, count, backward: false));

    // 后退一步 + 头部回绕到末尾。
    [Theory]
    [InlineData(2, 3, 1)]
    [InlineData(1, 3, 0)]
    [InlineData(0, 3, 2)]
    public void 向后步进并回绕(int current, int count, int expected)
        => Assert.Equal(expected, WindowListMerger.NextIndex(current, count, backward: true));

    // 负 current（尚未选中任何项 / -1 哨兵）：前进取首项，后退取末项；更小的负数同样收敛。
    [Theory]
    [InlineData(-1, false, 0)]
    [InlineData(-1, true, 4)]
    [InlineData(-7, false, 0)]
    [InlineData(-7, true, 4)]
    public void 负索引当作未选中处理(int current, bool backward, int expected)
        => Assert.Equal(expected, WindowListMerger.NextIndex(current, 5, backward));

    // 越界 current（列表在步进过程中变短）：按取模收敛，不抛异常。9 → 归一为 4，前进回到 0、后退到 3。
    [Theory]
    [InlineData(9, false, 0)]
    [InlineData(9, true, 3)]
    public void 越界索引取模收敛(int current, bool backward, int expected)
        => Assert.Equal(expected, WindowListMerger.NextIndex(current, 5, backward));

    // count<=0 → -1（空列表无法选中）。
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void 空列表返回无效索引(int count)
    {
        Assert.Equal(-1, WindowListMerger.NextIndex(0, count, backward: false));
        Assert.Equal(-1, WindowListMerger.NextIndex(-1, count, backward: true));
    }

    // 连续步进走完一整圈回到起点（循环性不变量）。
    [Fact]
    public void 连续步进一圈回到起点()
    {
        int idx = -1;
        for (int i = 0; i < 5; i++) idx = WindowListMerger.NextIndex(idx, 5, backward: false);
        Assert.Equal(4, idx);
        idx = WindowListMerger.NextIndex(idx, 5, backward: false);
        Assert.Equal(0, idx);
        for (int i = 0; i < 5; i++) idx = WindowListMerger.NextIndex(idx, 5, backward: true);
        Assert.Equal(0, idx);
    }
}
