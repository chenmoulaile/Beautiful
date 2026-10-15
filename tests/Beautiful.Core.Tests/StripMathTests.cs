using System;
using Beautiful.Core.Layout;
using Xunit;

/// <summary>
/// 条带几何纯函数测试（v3 T1）。
/// <para>
/// 本 VM 只有 1024x768 单显 100% DPI，贴边/多屏/高 DPI 分支无法用截图证明，
/// 因此这些不变量必须在纯函数层锁住：顶栏 <c>window.Top == rcMonitor.Top</c>、
/// Dock <c>window.Bottom == reserve.Bottom</c> 且 <c>window.Top = reserve.Top - headroom</c>
/// （放大余量不占工作区，v3 §1.C 裁定）。
/// </para>
/// </summary>
public class StripMathTests
{
    private static readonly PhysicalRect Primary = new(0, 0, 1024, 768);
    private static readonly PhysicalRect Secondary = new(-1280, 0, 0, 1024);

    // ---------- 贴边不变量 ----------

    [Fact]
    public void 顶边预留带_顶边恒等于显示器上沿_宽度恒为全宽()
    {
        var r = StripMath.Reserve(Primary, 30, StripEdge.Top);
        Assert.Equal(0, r.Top);                       // 顶栏贴边不变量：reserve.Top == rcMonitor.Top
        Assert.Equal(30, r.Bottom);
        Assert.Equal(30, r.Height);                   // 预留的工作区只有条带本身
        Assert.Equal(Primary.Left, r.Left);
        Assert.Equal(Primary.Right, r.Right);
        Assert.Equal(Primary.Width, r.Width);
    }

    [Fact]
    public void 顶边窗口矩形恒等于预留带_故贴边不变量传递到窗口()
    {
        var r = StripMath.Reserve(Primary, 30, StripEdge.Top);
        var w = StripMath.WindowRect(r, 44, StripEdge.Top); // 顶栏即便误传余量也不越界生长
        Assert.Equal(r, w);
        Assert.Equal(Primary.Top, w.Top);             // 恒成立：window.Top == rcMonitor.Top
    }

    [Fact]
    public void 底边预留带_底边恒等于显示器下沿()
    {
        var r = StripMath.Reserve(Primary, 76, StripEdge.Bottom);
        Assert.Equal(768, r.Bottom);                  // 贴底无缝：reserve.Bottom == rcMonitor.Bottom
        Assert.Equal(768 - 76, r.Top);
        Assert.Equal(76, r.Height);
        Assert.Equal(Primary.Width, r.Width);
    }

    [Fact]
    public void 底边窗口在预留带之上多出余量_底边仍同底()
    {
        var r = StripMath.Reserve(Primary, 76, StripEdge.Bottom);
        var w = StripMath.WindowRect(r, 22, StripEdge.Bottom);
        Assert.Equal(r.Bottom, w.Bottom);             // 窗口底边不让位（无缝仍无缝）
        Assert.Equal(r.Top - 22, w.Top);              // 越界生长只向上，进的是桌面不是工作区
        Assert.Equal(98, w.Height);
        Assert.Equal(r.Width, w.Width);
    }

    [Fact]
    public void Band_一次算清_预留带只含面板_窗口含余量()
    {
        var (r, w) = StripMath.Band(Primary, 76, 22, 1.0, StripEdge.Bottom);
        Assert.Equal(76, r.Height);                   // AppBar 上报高 = 可见面板高（v3 §1.C）
        Assert.Equal(98, w.Height);
        Assert.Equal(22, w.Height - r.Height);        // 工作区净得 = headroom
        var (tr, tw) = StripMath.Band(Primary, 30, 0, 1.0, StripEdge.Top);
        Assert.Equal(tr, tw);
        Assert.Equal(30, tr.Height);
    }

    // ---------- DIP ↔ 物理 ----------

    [Theory]
    [InlineData(100, 1.0)]
    [InlineData(125, 1.25)]
    [InlineData(150, 1.5)]
    [InlineData(175, 1.75)]
    public void 各缩放_DIP往返物理_偏差不超过半个物理像素(int dpi, double scale)
    {
        foreach (var dip in new double[] { 30, 44, 76, 98, 0, 1 })
        {
            var physical = StripMath.ToPhysical(dip, scale);
            var back = StripMath.ToDip(physical, scale);
            Assert.True(Math.Abs(back - dip) * scale <= 0.5 + 1e-9,
                $"{dip}DIP@{dpi}% → {physical}px → {back}DIP 偏差超过半 px");
        }
    }

    [Theory]
    [InlineData(100, 1.0, 44, 44)]
    [InlineData(125, 1.25, 44, 55)]
    [InlineData(150, 1.5, 44, 66)]
    [InlineData(175, 1.75, 44, 77)]
    [InlineData(175, 1.75, 76, 133)]    // 76 × 1.75 = 133 恰好整数
    [InlineData(125, 1.25, 30, 38)]     // 30 × 1.25 = 37.5 → 银行家舍入到 38（偶数）
    [InlineData(125, 1.25, 2, 2)]       // 2 × 1.25 = 2.5 → 2（同为偶数，方向与上一条一致）
    public void 面板高按缩放取整(int dpi, double scale, int dip, int expectedPhysical)
    {
        var actual = StripMath.ToPhysical(dip, scale);
        Assert.True(actual == expectedPhysical, $"{dip} DIP @ {dpi}% 应为 {expectedPhysical} 物理 px，实际 {actual}");
    }

    [Fact]
    public void 取整规则与App既有口径逐字一致_MathRound默认银行家舍入()
    {
        // 2 DIP @125% = 2.5px：MidpointRounding.ToEven → 2（若改成 AwayFromZero 会变 3，
        // 半数取值上产生 1px 系统性偏移，既有"gap 0px"实测日志就无法比对）。
        Assert.Equal((int)Math.Round(2.0 * 1.25), StripMath.ToPhysical(2.0, 1.25));
        Assert.Equal((int)Math.Round(6.0 * 1.75), StripMath.ToPhysical(6.0, 1.75));
    }

    [Fact]
    public void 缩放非法或零_按一比一处理且不出NaN()
    {
        foreach (var bad in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
        {
            Assert.Equal(30, StripMath.ToPhysical(30, bad));
            Assert.Equal(30, StripMath.ToDip(30, bad));
        }
    }

    [Fact]
    public void 负高度钳为零_不产生倒挂矩形()
    {
        var r = StripMath.Reserve(Primary, -50, StripEdge.Bottom);
        Assert.Equal(768, r.Top);
        Assert.Equal(0, r.Height);                    // Bottom == Top：倒挂（负高）会让 SetWindowPos 失败
        var w = StripMath.WindowRect(r, -22, StripEdge.Bottom);
        Assert.Equal(r, w);
    }

    // ---------- 多显示器 ----------

    [Fact]
    public void 负X副屏_几何随该屏左右缘_不掺主屏假设()
    {
        // 典型桌面拓扑：副屏在主屏左侧 → rcMonitor = (-1280,0)-(0,1024)
        var (r, w) = StripMath.Band(Secondary, 76, 22, 1.0, StripEdge.Bottom);
        Assert.Equal(-1280, r.Left);
        Assert.Equal(0, r.Right);
        Assert.Equal(1280, r.Width);                  // 铺满副屏全宽（不是主屏 1024）
        Assert.Equal(1024, r.Bottom);                 // 贴的是副屏自己的下沿
        Assert.Equal(1024 - 76, r.Top);
        Assert.Equal(1024 - 76 - 22, w.Top);
        Assert.Equal(1024, w.Bottom);

        var top = StripMath.Reserve(Secondary, 30, StripEdge.Top);
        Assert.Equal(0, top.Top);                     // 副屏上沿同样是 0（这里 Left 为负、Top 为 0）
        Assert.Equal(-1280, top.Left);
        Assert.Equal(1280, top.Width);
    }

    [Fact]
    public void 副屏原点非零_含纵向错位_底边取该屏自己的底()
    {
        // 副屏在主屏右侧且高出 180px：(1920,-180)-(3840,900)
        var mon = new PhysicalRect(1920, -180, 3840, 900);
        var (r, w) = StripMath.Band(mon, 76, 22, 1.5, StripEdge.Bottom);
        Assert.Equal(900, r.Bottom);
        Assert.Equal(900 - 114, r.Top);               // 76 DIP × 1.5 = 114 物理 px
        Assert.Equal(r.Top - 33, w.Top);              // 22 DIP × 1.5 = 33 物理 px
        Assert.Equal(1920, r.Left);
        Assert.Equal(3840, r.Right);

        var top = StripMath.Reserve(mon, 45, StripEdge.Top);
        Assert.Equal(-180, top.Top);                  // 顶栏在错位副屏上贴的是 -180，不是 0
        Assert.Equal(45, top.Height);
    }

    [Fact]
    public void QUERYPOS返回的可用矩形可直接当monitor传_底边让位任务栏时窗口悬其上方()
    {
        // Keep 策略：shell 把可用底边上收到 720（任务栏上沿），底边不变量随之落到 720
        var available = new PhysicalRect(0, 0, 1024, 720);
        var (r, w) = StripMath.Band(available, 76, 22, 1.0, StripEdge.Bottom);
        Assert.Equal(720, r.Bottom);
        Assert.Equal(720 - 76 - 22, w.Top);
        Assert.True(w.Top > 0);                       // 面板 + 余量都还在屏内
    }

    [Fact]
    public void 矩形宽高属性与PhysicalRect相等性()
    {
        Assert.Equal(1024, Primary.Width);
        Assert.Equal(768, Primary.Height);
        Assert.Equal(new PhysicalRect(0, 0, 1024, 768), Primary);   // record struct 值相等（日志比对用）
        Assert.NotEqual(Primary, Secondary);
    }
}
