using Beautiful.Core.Themes;
using Xunit;

/// <summary>
/// 图标主题包 manifest 纯解析与校验：版本/名称必查、引用只认 asset: 或 icons/*.png 相对路径、
/// 包内对账（引用必须存在）、路径穿越（..、反斜杠、绝对路径）一律拒绝。
/// </summary>
public class ThemePackTests
{
    private const string ValidManifest = """
        { "SchemaVersion": 1, "Name": "我的主题", "Author": "someone", "Description": "d",
          "IconOverrides": { "code": "icons/code.png", "finder": "asset:finder" } }
        """;

    [Fact]
    public void 合法manifest解析通过()
    {
        Assert.True(ThemePack.TryParseManifest(ValidManifest, out var manifest, out var errors));
        Assert.Empty(errors);
        Assert.Equal("我的主题", manifest!.Name);
        Assert.Equal(2, manifest.IconOverrides.Count);
        Assert.Equal("icons/code.png", manifest.IconOverrides["code"]);
        Assert.Equal("asset:finder", manifest.IconOverrides["finder"]);
    }

    [Fact]
    public void 导出再导入Roundtrip()
    {
        var json = ThemePack.BuildManifestJson("我的主题", "someone", "d",
            new Dictionary<string, string> { ["code"] = "asset:code", ["finder"] = "icons/finder.png" });

        Assert.True(ThemePack.TryParseManifest(json, out var manifest, out var errors));
        Assert.Empty(errors);
        Assert.Equal("我的主题", manifest!.Name);
        // asset 引用原样保留（引用目标机内置图集、不占包体积，导入端按本机图集键解析——
        // 见 IconThemeService.ImportPack）；文件覆盖统一转 icons/<键>.png。
        Assert.Equal("asset:code", manifest.IconOverrides["code"]);
        Assert.Equal("icons/finder.png", manifest.IconOverrides["finder"]);
    }

    [Theory]
    [InlineData("")]                                                     // 空
    [InlineData("不是JSON")]                                             // 语法
    [InlineData("""{ "Name": "x", "IconOverrides": { "a": "icons/a.png" } }""")]           // 缺版本
    [InlineData("""{ "SchemaVersion": 2, "Name": "x", "IconOverrides": { "a": "icons/a.png" } }""")] // 版本
    [InlineData("""{ "SchemaVersion": 1, "IconOverrides": { "a": "icons/a.png" } }""")]      // 缺名称
    [InlineData("""{ "SchemaVersion": 1, "Name": "", "IconOverrides": { "a": "icons/a.png" } }""")]  // 空名称
    [InlineData("""{ "SchemaVersion": 1, "Name": "x" }""")]                                  // 缺 overrides
    [InlineData("""{ "SchemaVersion": 1, "Name": "x", "IconOverrides": {} }""")]             // 空 overrides
    public void 结构不合规整体失败(string json)
    {
        Assert.False(ThemePack.TryParseManifest(json, out var manifest, out var errors));
        Assert.Null(manifest);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void 引用形状校验拒绝路径穿越与越界路径()
    {
        Assert.Null(ThemePack.ValidateReferenceShape("icons/code.png"));
        Assert.Null(ThemePack.ValidateReferenceShape("asset:finder"));
        Assert.NotNull(ThemePack.ValidateReferenceShape("icons/../evil.png"));
        Assert.NotNull(ThemePack.ValidateReferenceShape(@"icons\code.png"));
        Assert.NotNull(ThemePack.ValidateReferenceShape(@"C:\abs\code.png"));
        Assert.NotNull(ThemePack.ValidateReferenceShape("code.png"));                  // 不在 icons/ 下
        Assert.NotNull(ThemePack.ValidateReferenceShape("icons/code.ico"));            // 只认 png
        Assert.NotNull(ThemePack.ValidateReferenceShape("icons/sub/code.png"));        // 不允许多层
        Assert.NotNull(ThemePack.ValidateReferenceShape("icons/"));                    // 目录
    }

    [Fact]
    public void 包内对账_引用缺失报错()
    {
        var ok = ThemePack.TryParseManifest(ValidManifest, out var manifest, out _);
        Assert.True(ok);

        var entries = new[] { "theme.json", "icons/code.png", "icons/code.png.bak" };
        Assert.Empty(ThemePack.ValidateAgainstArchive(manifest!, entries));   // code.png 存在；asset 不需要对账

        var missing = ThemePack.ValidateAgainstArchive(manifest!, new[] { "theme.json" });
        Assert.Single(missing);
        Assert.Contains("code", missing[0]);
    }

    [Fact]
    public void 包内对账_条目名大小写不敏感()
    {
        var ok = ThemePack.TryParseManifest(ValidManifest, out var manifest, out _);
        Assert.True(ok);
        // zip 条目大小写与 manifest 不一致也允许命中（Windows/zip 语义），但绝不允许缺失。
        Assert.Empty(ThemePack.ValidateAgainstArchive(manifest!, new[] { "ICONS/CODE.PNG" }));
    }

    [Fact]
    public void IconEntryName按应用键命名()
    {
        Assert.Equal("icons/code.png", ThemePack.IconEntryName("code"));
        Assert.Equal("icons/trash_empty.png", ThemePack.IconEntryName("trash_empty"));
    }
}
