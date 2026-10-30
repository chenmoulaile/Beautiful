using Beautiful.Core.Design;
using Xunit;

/// <summary>
/// v4 Phase 1 T5：SF Pro 解析链取证（D-FONT）。三种形态必须都能给出可渲染的 FontFamily：
/// 内嵌资产优先、系统已装次之、CI/公开仓库回退 Segoe——字体缺失时应用必须完整可用。
/// </summary>
public class FontResolutionTests
{
    [Fact]
    public void 内嵌资产优先于系统字体()
    {
        var choice = FontResolution.Resolve(fontAssetsBundled: true, sfProInstalled: true);
        Assert.Contains("pack://application:,,,/Assets/Fonts/#SF Pro Text", choice.Family);
        Assert.Contains("Segoe UI", choice.Family);          // 逗号回退链
        Assert.Equal("SF Pro Text(内嵌)", choice.Source);
    }

    [Fact]
    public void 无内嵌但系统已装_用系统SfPro()
    {
        var choice = FontResolution.Resolve(fontAssetsBundled: false, sfProInstalled: true);
        Assert.Equal(FontResolution.SystemFamily, choice.Family);
        Assert.Equal("SF Pro Text(系统)", choice.Source);
    }

    [Fact]
    public void 两者都没有_回退Segoe且应用可渲染()
    {
        var choice = FontResolution.Resolve(fontAssetsBundled: false, sfProInstalled: false);
        Assert.Equal(FontResolution.FallbackFamily, choice.Family);
        Assert.Contains("Segoe UI Variable", choice.Family);
        Assert.Equal("Segoe UI Variable(回退)", choice.Source);
    }

    [Fact]
    public void 日志与关于页文案带来源()
    {
        var choice = FontResolution.Resolve(false, false);
        Assert.Contains("字体解析：", FontResolution.LogLine(choice));
        Assert.Contains("Segoe UI Variable(回退)", FontResolution.LogLine(choice));
        Assert.StartsWith("字体：", FontResolution.Describe(choice));
    }
}
