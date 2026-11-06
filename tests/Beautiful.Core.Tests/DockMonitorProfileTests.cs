using Beautiful.Core.Dock;
using Xunit;

/// <summary>
/// 每显示器 Dock 覆盖配置（对标 MyDockFinder 多屏独立设置）：规范化（Monitor 键/Edge/IconSize）
/// 与解析（序号优先、主屏其次、null 字段跟随全局）。核心红线：列表为空（单屏机器）时解析结果
/// 与全局值逐字段一致，即"单屏行为与现状完全一致，不回归"。
/// </summary>
public class DockMonitorProfileTests
{
    // ---------- 单条规范化 ----------

    [Fact]
    public void Monitor键_auto空白非法都收敛primary_序号归一()
    {
        Assert.Equal("primary", DockMonitorProfiles.Normalize(new DockMonitorProfile { Monitor = "primary" }).Monitor);
        Assert.Equal("primary", DockMonitorProfiles.Normalize(new DockMonitorProfile { Monitor = "auto" }).Monitor);
        Assert.Equal("primary", DockMonitorProfiles.Normalize(new DockMonitorProfile { Monitor = "" }).Monitor);
        Assert.Equal("primary", DockMonitorProfiles.Normalize(new DockMonitorProfile { Monitor = null! }).Monitor);
        Assert.Equal("primary", DockMonitorProfiles.Normalize(new DockMonitorProfile { Monitor = "zero" }).Monitor);
        Assert.Equal("2", DockMonitorProfiles.Normalize(new DockMonitorProfile { Monitor = "2" }).Monitor);
        Assert.Equal("2", DockMonitorProfiles.Normalize(new DockMonitorProfile { Monitor = " 2 " }).Monitor);
        Assert.Equal("3", DockMonitorProfiles.Normalize(new DockMonitorProfile { Monitor = "3.0" }).Monitor);
        Assert.Equal("primary", DockMonitorProfiles.Normalize(new DockMonitorProfile { Monitor = "17" }).Monitor); // 超出 16
        Assert.Equal("primary", DockMonitorProfiles.Normalize(new DockMonitorProfile { Monitor = "2.5" }).Monitor);
        Assert.Equal("primary", DockMonitorProfiles.Normalize(new DockMonitorProfile { Monitor = "0" }).Monitor);
    }

    [Fact]
    public void 停靠边_合法保留_非法收敛null跟随全局()
    {
        Assert.Equal("left", DockMonitorProfiles.Normalize(new DockMonitorProfile { Edge = "left" }).Edge);
        Assert.Equal("right", DockMonitorProfiles.Normalize(new DockMonitorProfile { Edge = " RIGHT " }).Edge);
        Assert.Equal("bottom", DockMonitorProfiles.Normalize(new DockMonitorProfile { Edge = "Bottom" }).Edge);
        Assert.Null(DockMonitorProfiles.Normalize(new DockMonitorProfile { Edge = "top" }).Edge);   // 无竖排实现
        Assert.Null(DockMonitorProfiles.Normalize(new DockMonitorProfile { Edge = "side" }).Edge);
        Assert.Null(DockMonitorProfiles.Normalize(new DockMonitorProfile { Edge = null }).Edge);
    }

    [Fact]
    public void 图标尺寸_钳到32到96_null保留()
    {
        Assert.Equal(48, DockMonitorProfiles.Normalize(new DockMonitorProfile { IconSize = 48 }).IconSize);
        Assert.Equal(32, DockMonitorProfiles.Normalize(new DockMonitorProfile { IconSize = 8 }).IconSize);
        Assert.Equal(96, DockMonitorProfiles.Normalize(new DockMonitorProfile { IconSize = 500 }).IconSize);
        Assert.Null(DockMonitorProfiles.Normalize(new DockMonitorProfile { IconSize = null }).IconSize);
    }

    [Fact]
    public void 空列表规范化_保持空_null条目滤除()
    {
        Assert.Empty(DockMonitorProfileList.Normalized(null));
        Assert.Empty(DockMonitorProfileList.Normalized([]));
        var withNull = DockMonitorProfileList.Normalized(
            new List<DockMonitorProfile> { null!, new DockMonitorProfile { Monitor = "2", IconSize = 999 } });
        Assert.Single(withNull);
        Assert.Equal(96, withNull[0].IconSize);
    }

    // ---------- 解析（Resolve） ----------

    [Fact]
    public void 无覆盖配置_逐字段等于全局_单屏零回归()
    {
        var plan = DockMonitorProfiles.Resolve([], monitorIndex: 1, isPrimary: true,
            globalHideMode: "dim", globalEdge: "bottom", globalIconSize: 56);
        Assert.Equal("dim", plan.HideMode);
        Assert.Equal("bottom", plan.Edge);
        Assert.Equal(56, plan.IconSize);

        var nullList = DockMonitorProfiles.Resolve(null, 2, false, "smart", "left", 44);
        Assert.Equal("smart", nullList.HideMode);
        Assert.Equal("left", nullList.Edge);
        Assert.Equal(44, nullList.IconSize);
    }

    [Fact]
    public void 序号精确匹配优先于主屏键()
    {
        var profiles = new List<DockMonitorProfile>
        {
            new() { Monitor = "primary", Edge = "right" },
            new() { Monitor = "1", Edge = "left" },
        };
        // 显示器 1 同时也是主屏：序号键命中
        var plan = DockMonitorProfiles.Resolve(profiles, monitorIndex: 1, isPrimary: true,
            globalHideMode: "off", globalEdge: "bottom", globalIconSize: 56);
        Assert.Equal("left", plan.Edge);
    }

    [Fact]
    public void 主屏键只在真主屏时生效()
    {
        var profiles = new List<DockMonitorProfile> { new() { Monitor = "primary", IconSize = 72 } };
        // 主屏：命中
        var onPrimary = DockMonitorProfiles.Resolve(profiles, monitorIndex: 2, isPrimary: true,
            globalHideMode: "off", globalEdge: "bottom", globalIconSize: 56);
        Assert.Equal(72, onPrimary.IconSize);
        // 非主屏：不命中，回落全局
        var onSecondary = DockMonitorProfiles.Resolve(profiles, monitorIndex: 2, isPrimary: false,
            globalHideMode: "off", globalEdge: "bottom", globalIconSize: 56);
        Assert.Equal(56, onSecondary.IconSize);
    }

    [Fact]
    public void null字段逐项跟随全局_非null字段生效()
    {
        var profiles = new List<DockMonitorProfile>
        {
            new() { Monitor = "1", Edge = "right", AutoHide = false, IconSize = 40 },
        };
        var plan = DockMonitorProfiles.Resolve(profiles, 1, true,
            globalHideMode: "auto", globalEdge: "bottom", globalIconSize: 56);
        Assert.Equal("right", plan.Edge);
        Assert.Equal(40, plan.IconSize);
        // AutoHide=false 把全局 auto 扳回 off（这一屏常显）
        Assert.Equal("off", plan.HideMode);
    }

    [Fact]
    public void 自动隐藏开关只在off与auto之间扳动()
    {
        // 全局 off + 此屏开 → auto
        Assert.Equal("auto", DockMonitorProfiles.EffectiveHideMode("off", true));
        // 全局 auto + 此屏关 → off
        Assert.Equal("off", DockMonitorProfiles.EffectiveHideMode("auto", false));
        // 全局 off + 此屏关 → 仍 off
        Assert.Equal("off", DockMonitorProfiles.EffectiveHideMode("off", false));
        // 全局 auto + 此屏开 → 仍 auto
        Assert.Equal("auto", DockMonitorProfiles.EffectiveHideMode("auto", true));
        // 条件档/半透明不受布尔影响（自动隐藏开关对它们无定义）
        Assert.Equal("dim", DockMonitorProfiles.EffectiveHideMode("dim", false));
        Assert.Equal("smart", DockMonitorProfiles.EffectiveHideMode("smart", true));
        // null = 跟随全局
        Assert.Equal("fullscreen", DockMonitorProfiles.EffectiveHideMode("fullscreen", null));
    }

    [Fact]
    public void 没有匹配的屏_全部回落全局()
    {
        var profiles = new List<DockMonitorProfile>
        {
            new() { Monitor = "2", Edge = "left", AutoHide = true, IconSize = 88 },
        };
        // 显示器 1（非主屏）没有覆盖 → 全局
        var plan = DockMonitorProfiles.Resolve(profiles, 1, false, "auto", "bottom", 56);
        Assert.Equal("auto", plan.HideMode);
        Assert.Equal("bottom", plan.Edge);
        Assert.Equal(56, plan.IconSize);
        // 显示器 2 命中
        var on2 = DockMonitorProfiles.Resolve(profiles, 2, false, "auto", "bottom", 56);
        Assert.Equal("auto", on2.HideMode);   // 全局已是 auto，AutoHide=true 不再改变
        Assert.Equal("left", on2.Edge);
        Assert.Equal(88, on2.IconSize);
    }

    [Fact]
    public void 序号越界_钳到合法区间再匹配()
    {
        var profiles = new List<DockMonitorProfile> { new() { Monitor = "16", IconSize = 80 } };
        var plan = DockMonitorProfiles.Resolve(profiles, monitorIndex: 99, isPrimary: false,
            globalHideMode: "off", globalEdge: "bottom", globalIconSize: 56);
        Assert.Equal(80, plan.IconSize);   // 99 钳到 16 → 命中
    }
}
