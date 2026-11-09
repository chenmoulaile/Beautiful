using System.Xml.Linq;
using Beautiful.Core.Launcher;
using Xunit;

/// <summary>
/// MSIX 包内 logo 候选解析取证（v5 ⑤ 补源）：候选次序就是启动器图标链第②级"包内 logo"的降级链，
/// 每个分支都断言到具体字符串/像素值，而不是只断言"非空"。全部为纯函数，不涉及磁盘与 shell。
/// 策略在本机 2026-11-09 探针演练过：179 包中 111 包命中，Family/记事本/计算器/画图/Store 全部
/// 拿到 targetsize-256 资产（探针与这里的期望值同源）。
/// </summary>
public class AppxLogoResolverTests
{
    // ---------- 清单构造 ----------

    private static XDocument Package(string applicationsXml)
        => XDocument.Parse(
            "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\"" +
            " xmlns:uap=\"http://schemas.microsoft.com/appx/manifest/uap/windows10\">" +
            "<Applications>" + applicationsXml + "</Applications></Package>");

    /// <summary>单应用清单：Application 用默认命名空间、VisualElements 用 uap 前缀——顺带钉死"命名空间无关"。</summary>
    private static XDocument SingleApp(string id, string logoAttrs)
        => Package(
            "<Application Id=\"" + id + "\" Executable=\"app.exe\">" +
            "<uap:VisualElements DisplayName=\"应用\" " + logoAttrs + "/>" +
            "</Application>");

    // ---------- 基名选择 ----------

    [Fact]
    public void Square44x44优先于其余基名()
    {
        var manifest = SingleApp("App",
            @"Square150x150Logo=""Assets\MedTile.png"" Square44x44Logo=""Assets\AppList.png"" Logo=""Assets\Logo.png""");

        var candidates = AppxLogoResolver.Candidates(manifest, 256);

        // 首选：Square44x44 的 256 unplated
        Assert.Equal(@"Assets\AppList.targetsize-256_altform-unplated.png", candidates[0].RelativePath);
        // Square44x44 的候选全部排在 Square150x150 之前
        var lastOf44 = candidates.Select((c, i) => (c, i)).Last(x => x.c.RelativePath.StartsWith(@"Assets\AppList.")).i;
        var firstOf150 = candidates.Select((c, i) => (c, i)).First(x => x.c.RelativePath.StartsWith(@"Assets\MedTile.")).i;
        Assert.True(lastOf44 < firstOf150);
        // Logo（无尺寸线索）垫底：整链最后一个候选是它的裸路径
        Assert.Equal(@"Assets\Logo.png", candidates[^1].RelativePath);
    }

    [Fact]
    public void applicationId命中对应_Application_未命中回落第一个带声明的()
    {
        var manifest = Package(
            "<Application Id=\"App1\" Executable=\"a.exe\">" +
            "<uap:VisualElements Square44x44Logo=\"Assets\\One.png\"/></Application>" +
            "<Application Id=\"App2\" Executable=\"b.exe\">" +
            "<uap:VisualElements Square44x44Logo=\"Assets\\Two.png\"/></Application>");

        Assert.Equal(@"Assets\Two.targetsize-256_altform-unplated.png",
            AppxLogoResolver.Candidates(manifest, 256, "App2")[0].RelativePath);
        // Id 比对不区分大小写（AUMID 的 '!' 后段与清单 Id 的大小写在真机上并不保证一致）
        Assert.Equal(@"Assets\Two.targetsize-256_altform-unplated.png",
            AppxLogoResolver.Candidates(manifest, 256, "app2")[0].RelativePath);
        // 没给 / 给错 → 回落第一个带 logo 声明的 Application
        Assert.Equal(@"Assets\One.targetsize-256_altform-unplated.png",
            AppxLogoResolver.Candidates(manifest, 256, null)[0].RelativePath);
        Assert.Equal(@"Assets\One.targetsize-256_altform-unplated.png",
            AppxLogoResolver.Candidates(manifest, 256, "App9")[0].RelativePath);
    }

    [Fact]
    public void msresource_占位与空声明跳过()
    {
        var manifest = SingleApp("App",
            @"Square44x44Logo=""ms-resource:APP_Logo"" Square150x150Logo="""" Square310x310Logo=""Assets\Wide.png""");

        var candidates = AppxLogoResolver.Candidates(manifest, 256);
        // ms-resource 不是文件路径（替它找文件必是假路径）、空声明没得展开：直接落到下一个基名
        Assert.Equal(@"Assets\Wide.targetsize-256_altform-unplated.png", candidates[0].RelativePath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("<Package/>")]
    [InlineData("<Package><Applications/></Package>")]
    [InlineData("<Package><Applications><Application Id=\"App\" Executable=\"a.exe\"/></Applications></Package>")]
    [InlineData("<Package xmlns:uap=\"http://schemas.microsoft.com/appx/manifest/uap/windows10\"><Applications><Application Id=\"App\" Executable=\"a.exe\"><uap:VisualElements/></Application></Applications></Package>")]
    public void 清单缺失形态一律空列表而不是假候选(string? xml)
        => Assert.Empty(AppxLogoResolver.Candidates(xml is null ? null : XDocument.Parse(xml), 256));

    // ---------- targetsize 变体排序 ----------

    [Fact]
    public void 变体次序_unplated先于普通_命中档之后先大后小()
    {
        var candidates = AppxLogoResolver.VariantCandidates(@"Assets\AppList.png", 256);

        // want=256：命中档 256 打头，其后从大到小（96→16），unplated 恒在普通版之前
        var names = candidates.Select(c => c.RelativePath).ToList();
        Assert.Equal(@"Assets\AppList.targetsize-256_altform-unplated.png", names[0]);
        Assert.Equal(@"Assets\AppList.targetsize-256.png", names[1]);
        Assert.Equal(@"Assets\AppList.targetsize-96_altform-unplated.png", names[2]);
        Assert.Equal(@"Assets\AppList.targetsize-96.png", names[3]);
        Assert.Equal(@"Assets\AppList.targetsize-16.png", names[19]);
        // 尾部：裸资产兜底（"AppList" 无尺寸线索 → 没有 scale 阶，共 10 档 ×2 + 裸 = 21 条）
        Assert.Equal(21, names.Count);
        Assert.Equal(@"Assets\AppList.png", names[^1]);
        Assert.Equal(256, candidates[0].PixelSize);
        Assert.Equal(16, candidates[19].PixelSize);
        Assert.Equal(0, candidates[^1].PixelSize);   // 裸路径没有尺寸依据
    }

    [Fact]
    public void 变体次序_请求32时命中档优先_其后先大后小()
    {
        var names = AppxLogoResolver.VariantCandidates(@"Assets\AppList.png", 32)
            .Select(c => c.RelativePath).ToList();

        // 命中 32 → 其后"最小的更大档"（36,48,64,96,256）→ 无更大档再回头"最大的更小档"（30,24,20,16）
        Assert.Equal(@"Assets\AppList.targetsize-32_altform-unplated.png", names[0]);
        Assert.Equal(@"Assets\AppList.targetsize-36_altform-unplated.png", names[2]);
        Assert.Equal(@"Assets\AppList.targetsize-256.png", names[11]);
        Assert.Equal(@"Assets\AppList.targetsize-30.png", names[13]);
    }

    [Theory]
    [InlineData(0, "targetsize-16")]     // 压到下界：最小档命中
    [InlineData(9999, "targetsize-256")] // 压到上界：无更大档 → 最大档打头
    public void 请求尺寸压界后策略仍然成立(int requestedPx, string expectedPrefix)
    {
        var first = AppxLogoResolver.VariantCandidates(@"Assets\AppList.png", requestedPx)[0];
        Assert.Equal(@"Assets\AppList." + expectedPrefix + "_altform-unplated.png", first.RelativePath);
    }

    // ---------- scale 变体 ----------

    [Fact]
    public void scale_阶在targetsize之后_像素按声明尺寸折算()
    {
        var candidates = AppxLogoResolver.VariantCandidates(@"Images\Square44x44Logo.png", 176);

        var names = candidates.Select(c => c.RelativePath).ToList();
        // targetsize 阶在前（256 是 ≥176 的最小档）
        Assert.Equal(@"Images\Square44x44Logo.targetsize-256_altform-unplated.png", names[0]);
        // scale 阶：折算像素 176(400)/88(200)/66(150)/55(125)/44(100)，want=176 命中 400 档
        var scale400 = names.FindIndex(n => n.Contains(".scale-400_altform-unplated"));
        Assert.Equal(@"Images\Square44x44Logo.scale-400_altform-unplated.png", names[scale400]);
        Assert.True(scale400 > names.FindIndex(n => n.Contains("targetsize-16")));
        // scale 候选带折算像素（单测断言的就是"44 基名 × 400% = 176px"这笔账）
        Assert.Equal(176, candidates[scale400].PixelSize);
        Assert.Equal(88, candidates[names.FindIndex(n => n.EndsWith("Square44x44Logo.scale-200.png"))].PixelSize);
        // 尾部裸路径带声明尺寸
        Assert.Equal(@"Images\Square44x44Logo.png", names[^1]);
        Assert.Equal(44, candidates[^1].PixelSize);
    }

    [Fact]
    public void scale_阶内命中折算像素的档优先()
    {
        var names = AppxLogoResolver.VariantCandidates(@"Images\Square44x44Logo.png", 88)
            .Select(c => c.RelativePath).ToList();

        // want=88 → 折算后命中 scale-200（88px），压过更高倍的 scale-400（176px）
        var firstScale = names.Find(n => n.Contains(".scale-"));
        Assert.Equal(@"Images\Square44x44Logo.scale-200_altform-unplated.png", firstScale);
    }

    [Fact]
    public void 方形Tile_这类带前缀的基名也能解析出尺寸()
    {
        // Win8 世代系统包真机形态（Microsoft.Windows.XGpuEjectDialog）：SquareTile44x44.png
        var names = AppxLogoResolver.VariantCandidates(@"Assets\SquareTile44x44.png", 256)
            .Select(c => c.RelativePath).ToList();

        Assert.Contains(@"Assets\SquareTile44x44.scale-100.png", names);   // 没有 targetsize 资产时的真实出路
        Assert.Contains(@"Assets\SquareTile44x44.scale-400_altform-unplated.png", names);
    }

    [Fact]
    public void 无尺寸线索的基名没有scale阶()
    {
        var names = AppxLogoResolver.VariantCandidates(@"Assets\Logo.png", 256)
            .Select(c => c.RelativePath).ToList();

        Assert.DoesNotContain(names, n => n.Contains(".scale-"));
        Assert.Equal(21, names.Count);   // 10 档 targetsize ×2 + 裸
    }

    // ---------- 边界 ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ms-resource:Logo")]
    public void 空与占位基名给空列表(string? basePath)
        => Assert.Empty(AppxLogoResolver.VariantCandidates(basePath, 256));

    [Fact]
    public void 无扩展名的声明只给裸路径_不猜扩展名()
    {
        var candidates = AppxLogoResolver.VariantCandidates(@"Assets\Logo", 256);

        var single = Assert.Single(candidates);
        Assert.Equal(@"Assets\Logo", single.RelativePath);
    }

    [Fact]
    public void 斜杠目录的分隔符风格保持原样()
    {
        var first = AppxLogoResolver.VariantCandidates("Images/Square44x44Logo.png", 256)[0];

        Assert.Equal("Images/Square44x44Logo.targetsize-256_altform-unplated.png", first.RelativePath);
    }
}
