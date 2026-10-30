using Beautiful.Core.Finder;
using Xunit;

/// <summary>
/// Quick Look 类型路由测试（Phase 6-b 起步）。
/// <para>
/// 路由只做扩展名字符串判定，不碰文件系统：扩展名大小写不敏感、无扩展名/隐藏文件/非法路径串
/// 一律落到 <see cref="QuickLookKind.InfoCard"/>（信息卡是有效回退，不是错误）。
/// 内容预览只包含 Text/Image/Media/Pdf：InfoCard 是元数据卡，不算“内容预览”。
/// </para>
/// </summary>
public class QuickLookRouterTests
{
    // ---------- 文本 ----------

    [Theory]
    [InlineData("note.txt")]
    [InlineData("readme.md")]
    [InlineData("guide.markdown")]
    [InlineData("app.log")]
    [InlineData("data.json")]
    [InlineData("feed.xml")]
    [InlineData("config.yaml")]
    [InlineData("config.yml")]
    [InlineData("desktop.ini")]
    [InlineData("settings.cfg")]
    [InlineData("Program.cs")]
    [InlineData("main.cpp")]
    [InlineData("header.h")]
    [InlineData("header.hpp")]
    [InlineData("lib.c")]
    [InlineData("index.js")]
    [InlineData("types.ts")]
    [InlineData("App.tsx")]
    [InlineData("App.jsx")]
    [InlineData("script.py")]
    [InlineData("Main.java")]
    [InlineData("main.go")]
    [InlineData("lib.rs")]
    [InlineData("run.sh")]
    [InlineData("build.ps1")]
    [InlineData("start.bat")]
    [InlineData("start.cmd")]
    [InlineData("query.sql")]
    [InlineData("page.html")]
    [InlineData("page.htm")]
    [InlineData("style.css")]
    [InlineData("table.csv")]
    public void 文本扩展名_路由到Text(string name)
    {
        Assert.Equal(QuickLookKind.Text, QuickLookRouter.KindOf(name));
        Assert.Equal(QuickLookKind.Text, QuickLookRouter.KindOf(@"C:\Users\me\Documents\" + name));
    }

    // ---------- 图片 ----------

    [Theory]
    [InlineData("shot.png")]
    [InlineData("photo.jpg")]
    [InlineData("photo.jpeg")]
    [InlineData("anim.gif")]
    [InlineData("bitmap.bmp")]
    [InlineData("scan.tif")]
    [InlineData("scan.tiff")]
    [InlineData("app.ico")]
    public void 图片扩展名_路由到Image(string name)
    {
        Assert.Equal(QuickLookKind.Image, QuickLookRouter.KindOf(name));
        Assert.Equal(QuickLookKind.Image, QuickLookRouter.KindOf(@"C:\Pictures\" + name));
    }

    // ---------- 媒体 ----------

    [Theory]
    [InlineData("clip.mp4")]
    [InlineData("clip.m4v")]
    [InlineData("clip.mov")]
    [InlineData("clip.webm")]
    [InlineData("song.mp3")]
    [InlineData("voice.wav")]
    [InlineData("track.m4a")]
    public void 媒体扩展名_路由到Media(string name)
    {
        Assert.Equal(QuickLookKind.Media, QuickLookRouter.KindOf(name));
        Assert.Equal(QuickLookKind.Media, QuickLookRouter.KindOf(@"C:\Media\" + name));
    }

    // ---------- PDF ----------

    [Theory]
    [InlineData("manual.pdf")]
    [InlineData(@"D:\Docs\manual.pdf")]
    public void PDF扩展名_路由到Pdf(string path)
    {
        Assert.Equal(QuickLookKind.Pdf, QuickLookRouter.KindOf(path));
    }

    // ---------- 大小写 ----------

    [Theory]
    [InlineData("README.MD", QuickLookKind.Text)]
    [InlineData("Photo.JPG", QuickLookKind.Image)]
    [InlineData("Clip.MP4", QuickLookKind.Media)]
    [InlineData("Manual.PdF", QuickLookKind.Pdf)]
    [InlineData("archive.ZIP", QuickLookKind.InfoCard)]
    public void 扩展名大小写不敏感(string name, QuickLookKind expected)
    {
        Assert.Equal(expected, QuickLookRouter.KindOf(name));
    }

    // ---------- 回退 ----------

    [Theory]
    [InlineData("archive.zip")]
    [InlineData("setup.exe")]
    [InlineData("library.dll")]
    [InlineData("no-extension")]
    [InlineData(@"C:\Users\me\.gitignore")]
    [InlineData(@"C:\Users\me\folder")]
    [InlineData(@"C:\Users\me\trailing-dot.")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 未识别或无扩展名_回退到InfoCard(string? path)
    {
        Assert.Equal(QuickLookKind.InfoCard, QuickLookRouter.KindOf(path));
    }

    [Theory]
    [InlineData("bad|pipe.txt")]
    [InlineData("bad\0nul.txt")]
    [InlineData("\"quoted\".txt")]
    public void 非法路径串_不抛异常(string path)
    {
        // 是否恰好识别为 Text 取决于系统 Path 实现；契约是“绝不抛异常”，非法/不可解析时回退 InfoCard。
        var kind = QuickLookRouter.KindOf(path);
        Assert.True(kind is QuickLookKind.Text or QuickLookKind.InfoCard);
    }

    // ---------- IsContentPreview ----------

    [Theory]
    [InlineData(QuickLookKind.Text, true)]
    [InlineData(QuickLookKind.Image, true)]
    [InlineData(QuickLookKind.Media, true)]
    [InlineData(QuickLookKind.Pdf, true)]
    [InlineData(QuickLookKind.InfoCard, false)]
    public void 内容预览判定_只有四类真实内容(QuickLookKind kind, bool expected)
    {
        Assert.Equal(expected, QuickLookRouter.IsContentPreview(kind));
    }

    [Fact]
    public void 枚举类型齐全()
    {
        var kinds = Enum.GetValues<QuickLookKind>();
        Assert.Equal(5, kinds.Length);
        Assert.Contains(QuickLookKind.Text, kinds);
        Assert.Contains(QuickLookKind.Image, kinds);
        Assert.Contains(QuickLookKind.Media, kinds);
        Assert.Contains(QuickLookKind.Pdf, kinds);
        Assert.Contains(QuickLookKind.InfoCard, kinds);
    }
}
