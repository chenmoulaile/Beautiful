using Beautiful.Core.Dock;
using Beautiful.Core.Themes;
using Xunit;

/// <summary>
/// 图标主题覆盖键规则：普通应用与 Dock 分组键（DockAppKey）同源、内置格固定键、
/// 非应用身份（shell:/::{/ms-）一律空键、候选键与 MacIconCatalog.SourceFor 同一优先级。
/// </summary>
public class IconThemeKeyTests
{
    [Theory]
    [InlineData(@"C:\Windows\explorer.exe", "explorer")]
    [InlineData(@"C:\Program Files\Microsoft VS Code\Code.exe", "code")]
    [InlineData(@"C:\Program Files\Mozilla Firefox\firefox.exe", "firefox")]
    [InlineData("msedge.exe", "msedge")]
    public void 普通应用与Dock分组键同源(string source, string expected)
    {
        Assert.Equal(expected, IconThemeKey.Of(source));
        Assert.Equal(DockAppKey.Of(source), IconThemeKey.Of(source));
    }

    [Fact]
    public void 内置功能格折成固定键()
    {
        Assert.Equal("finder", IconThemeKey.Of("builtin:finder"));
        Assert.Equal("launchpad", IconThemeKey.Of("builtin:launchpad"));
        Assert.Equal("trash", IconThemeKey.Of("::{645FF040-5081-101B-9F08-00AA002F954E}"));
        // 大小写不敏感（与 MacIconCatalog.KeysFor 的内置分支同口径）
        Assert.Equal("finder", IconThemeKey.Of("BUILTIN:Finder"));
    }

    [Theory]
    [InlineData("shell:RecycleBinFolder")]
    [InlineData("builtin:widget:xyz")]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")]
    [InlineData("ms-settings:display")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 非应用身份一律空键(string? source)
    {
        Assert.Equal(string.Empty, IconThemeKey.Of(source));
    }

    [Fact]
    public void 访达启动台候选键只认IconSource()
    {
        // 访达的 LaunchPath 被设成了用户目录——拿去折键会折出「以用户名命名的应用」这种假键，
        // 必须像 MacIconCatalog.SourceFor 一样只认 IconSource。
        var finder = IconThemeKey.CandidatesFor(DockItemKind.Finder, @"C:\Users\someone", "builtin:finder");
        Assert.Single(finder);
        Assert.Equal("finder", finder[0]);

        var launchpad = IconThemeKey.CandidatesFor(DockItemKind.Launchpad, "", "builtin:launchpad");
        Assert.Single(launchpad);
        Assert.Equal("launchpad", launchpad[0]);
    }

    [Fact]
    public void 普通条目LaunchPath优先IconSource兜底()
    {
        // .lnk 显示名条目：LaunchPath 折不出键（空格规则），IconSource 已是解析后的目标 exe。
        var keys = IconThemeKey.CandidatesFor(DockItemKind.Pinned, "Visual Studio Code.lnk", @"C:\x\Code.exe");
        Assert.Single(keys);
        Assert.Equal("code", keys[0]);
    }

    [Fact]
    public void 回收站候选键来自clsid()
    {
        var keys = IconThemeKey.CandidatesFor(DockItemKind.RecycleBin, "shell:RecycleBinFolder",
            "::{645FF040-5081-101B-9F08-00AA002F954E}");
        Assert.Single(keys);
        Assert.Equal("trash", keys[0]);
    }

    [Fact]
    public void 候选键去重去空()
    {
        // LaunchPath 与 IconSource 折出同一个键 → 只留一条；两者都折不出键 → 空序列。
        Assert.Single(IconThemeKey.CandidatesFor(DockItemKind.Pinned, @"C:\a\code.exe", @"C:\b\code.exe"));
        Assert.Empty(IconThemeKey.CandidatesFor(DockItemKind.Pinned, "", null));
    }
}
