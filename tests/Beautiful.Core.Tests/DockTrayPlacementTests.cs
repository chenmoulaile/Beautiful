using Beautiful.Core.Dock;
using Beautiful.Core.Settings;
using Xunit;

/// <summary>
/// 托盘收纳列 applet（5.0.0 未闭合项④）的落位规则：挂点白名单规范化、后缀块插入下标，
/// 以及"Tray 与行尾邻居之间不产生分隔符"（<see cref="DockSeparatorPlan"/> 的既有边界清单
/// 不认识 Tray，行为必须保持——多一个 applet 不许多出一道细线）。
/// </summary>
public class DockTrayPlacementTests
{
    // ---------- 挂点规范化 ----------

    [Fact]
    public void 挂点_大小写空白归一_非法与空收敛默认widget()
    {
        Assert.Equal("widget", DockTrayPlacement.NormalizePosition("widget"));
        Assert.Equal("end", DockTrayPlacement.NormalizePosition("end"));
        Assert.Equal("end", DockTrayPlacement.NormalizePosition(" END "));
        Assert.Equal("widget", DockTrayPlacement.NormalizePosition("Widget"));
        Assert.Equal("widget", DockTrayPlacement.NormalizePosition(""));
        Assert.Equal("widget", DockTrayPlacement.NormalizePosition(null));
        Assert.Equal("widget", DockTrayPlacement.NormalizePosition("row"));    // 未知值自愈
        Assert.Equal("widget", DockTrayPlacement.NormalizePosition("tail"));
    }

    // ---------- 后缀块插入下标 ----------

    [Fact]
    public void 开关关_不插入()
    {
        Assert.Equal(-1, DockTrayPlacement.SuffixIndex(enabled: false, "widget", widgetEnabled: true));
        Assert.Equal(-1, DockTrayPlacement.SuffixIndex(enabled: false, "end", widgetEnabled: true));
        Assert.Equal(-1, DockTrayPlacement.SuffixIndex(enabled: false, null, widgetEnabled: false));
    }

    [Fact]
    public void 默认挂点_永远在小组件之前_小组件开关不影响()
    {
        Assert.Equal(2, DockTrayPlacement.SuffixIndex(enabled: true, "widget", widgetEnabled: true));
        Assert.Equal(2, DockTrayPlacement.SuffixIndex(enabled: true, "widget", widgetEnabled: false));
        Assert.Equal(2, DockTrayPlacement.SuffixIndex(enabled: true, null, widgetEnabled: true));
    }

    [Fact]
    public void 行尾档_小组件开着排它后面_关着收敛为行尾()
    {
        Assert.Equal(3, DockTrayPlacement.SuffixIndex(enabled: true, "end", widgetEnabled: true));
        Assert.Equal(2, DockTrayPlacement.SuffixIndex(enabled: true, "end", widgetEnabled: false));
    }

    // ---------- 与分隔符规则的相容性 ----------

    [Fact]
    public void Tray与行尾邻居之间不产生分隔符()
    {
        // 后缀块 [回收站, 媒体, 托盘, 小组件]（widget 挂点）：托盘不应触发任何边界细线。
        var kinds = new[] { DockItemKind.Finder, DockItemKind.Pinned, DockItemKind.RecycleBin,
                            DockItemKind.Media, DockItemKind.Tray, DockItemKind.Widget };
        var slots = DockSeparatorPlan.Slots(kinds, enabled: true);
        Assert.DoesNotContain(slots, s => s.Index == 5);   // 小组件之前不插
        Assert.DoesNotContain(slots, s => s.Index == 4);   // 托盘之前不插
        // 既有边界保持：回收站之前那一道（i=2）仍在，且只有它。
        var slot = Assert.Single(slots);
        Assert.Equal(2, slot.Index);
        Assert.Equal(DockSeparatorRole.TrashBoundary, slot.Role);

        // 行尾档 [回收站, 媒体, 小组件, 托盘]：托盘与小组件之间同样不插。
        var kindsEnd = new[] { DockItemKind.Finder, DockItemKind.Pinned, DockItemKind.RecycleBin,
                               DockItemKind.Media, DockItemKind.Widget, DockItemKind.Tray };
        var slotsEnd = DockSeparatorPlan.Slots(kindsEnd, enabled: true);
        Assert.DoesNotContain(slotsEnd, s => s.Index == 5);   // 托盘之前不插
        Assert.Single(slotsEnd);
    }

    [Fact]
    public void Tray参与放大场与命中_与其它功能格同集合()
    {
        // 只有分隔符被排除：托盘格是可点功能格，悬停/点击必须命中（否则弹层永远打不开）。
        Assert.True(DockSeparatorPlan.IsMagnifiable(DockItemKind.Tray));
        Assert.True(DockSeparatorPlan.IsInteractive(DockItemKind.Tray));
    }

    // ---------- 设置键收敛（AppSettings.Normalized 的 Canonical 一条龙） ----------

    [Fact]
    public void 设置键_默认关_挂点非法值自愈为widget()
    {
        var defaults = new AppSettings();
        Assert.False(defaults.DockTrayEnabled);   // 可选扩展，默认关
        Assert.Equal("widget", defaults.DockTrayPosition);
        Assert.Equal("end", new AppSettings { DockTrayPosition = " END " }.Normalized().DockTrayPosition);   // Trim+小写归一后是合法值（与 NormalizePosition 的" END "→end 同源）
        Assert.Equal("end", new AppSettings { DockTrayPosition = "End" }.Normalized().DockTrayPosition);
        Assert.Equal("widget", new AppSettings { DockTrayPosition = "top" }.Normalized().DockTrayPosition);
    }
}
