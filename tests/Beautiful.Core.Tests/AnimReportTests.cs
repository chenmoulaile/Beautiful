using Beautiful.Core.Animation;
using Xunit;

/// <summary>
/// 结构化日志与几何对账的测试。<b>日志是冲刺期间唯一的证据</b>：格式一旦漂移，
/// diag 脚本的 [动画] 节就统计不出来，用户报"没动画"时又只能人工翻日志。
/// </summary>
public class AnimReportTests
{
    [Fact]
    public void 触发行包含全部字段且顺序固定()
    {
        var line = AnimReport.Trigger("Dock", "PrintWindow", "mesh", 500, 31, 8.4);
        Assert.StartsWith("动画触发：", line);
        Assert.Contains("源=Dock", line);
        Assert.Contains("层=PrintWindow", line);
        Assert.Contains("引擎=mesh", line);
        Assert.Contains("时长=500ms", line);
        Assert.Contains("帧数=31", line);
        Assert.Contains("P95=8.4ms", line);
    }

    [Fact]
    public void 未播行包含源层因三要素()
    {
        var line = AnimReport.Skipped("MINIMIZESTART", "缓存", "到手时已最小化且缓存帧过期");
        Assert.StartsWith("动画未播：", line);
        Assert.Contains("源=MINIMIZESTART", line);
        Assert.Contains("层=缓存", line);
        Assert.Contains("因=到手时已最小化且缓存帧过期", line);
    }

    [Fact]
    public void 未播行可带细节()
    {
        var line = AnimReport.Skipped("Dock", "无", "窗口过小", "40×30");
        Assert.Contains("细节=40×30", line);
    }

    [Fact]
    public void 空字段写未知而不是留空()
    {
        var line = AnimReport.Skipped(null!, "", "抓帧不可用");
        Assert.Contains("源=未知", line);
        Assert.Contains("层=未知", line);
    }

    [Fact]
    public void 几何一致时不报错()
    {
        var src = new AnimRect(100, 100, 800, 600);
        var dwm = new AnimRect(100, 100, 800, 600);
        var icon = new AnimRect(900, 1040, 96, 96);
        var ghost = new AnimRect(100, 100, 800, 600);
        Assert.Equal("一致", AnimReport.GeometryVerdict(src, dwm, icon, ghost));
    }

    [Fact]
    public void 幽灵尺寸是源窗口的两倍判DPI错位()
    {
        var src = new AnimRect(100, 100, 800, 600);
        var ghost = new AnimRect(100, 100, 1600, 1200);
        Assert.StartsWith("DPI倍率错位", AnimReport.GeometryVerdict(src, src, new AnimRect(0, 1000, 96, 96), ghost));
    }

    [Fact]
    public void 幽灵落在源矩形之外判越界()
    {
        var src = new AnimRect(100, 100, 800, 600);
        var ghost = new AnimRect(400, 100, 800, 600);
        Assert.Equal("幽灵越界", AnimReport.GeometryVerdict(src, src, new AnimRect(0, 1000, 96, 96), ghost));
    }

    [Fact]
    public void 图标宽度为零判图标缺失()
    {
        var src = new AnimRect(100, 100, 800, 600);
        Assert.Equal("图标缺失", AnimReport.GeometryVerdict(src, src, new AnimRect(0, 1000, 0, 0), src));
    }

    [Fact]
    public void 无幽灵时如实写未起播()
    {
        var src = new AnimRect(100, 100, 800, 600);
        Assert.Equal("无幽灵（未起播）", AnimReport.GeometryVerdict(src, src, new AnimRect(0, 1000, 96, 96), null));
    }

    [Fact]
    public void 有边框窗口的GetWindowRect与DWM差一圈不算错()
    {
        // 本机实测：最大化 Chrome 的 GetWindowRect 外扩 13px，DWM 可见矩形才是真像素
        var src = AnimRect.FromLtrb(-13, -13, 2893, 1837);
        var dwm = AnimRect.FromLtrb(0, 0, 2880, 1824);
        Assert.Equal("一致", AnimReport.GeometryVerdict(src, dwm, new AnimRect(100, 100, 96, 96), src));
    }

    [Fact]
    public void 几何对账行带三方矩形()
    {
        var src = new AnimRect(100, 100, 800, 600);
        var line = AnimReport.Geometry("Dock/PrintWindow", src, src, new AnimRect(900, 1040, 96, 96), src);
        Assert.StartsWith("几何对账：", line);
        Assert.Contains("GetWindowRect=100,100 800×600", line);
        Assert.Contains("DWM=100,100 800×600", line);
        Assert.Contains("图标=900,1040 96×96", line);
        Assert.Contains("结论=一致", line);
    }
}
