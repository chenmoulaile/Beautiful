using System;
using System.IO;
using Beautiful.Core.Dock;
using Xunit;

/// <summary>
/// 自定义图标规则测试：只认能解码的图片格式、拒绝空文件/超大文件/不存在的路径。
/// 真机上"选了张视频当图标"这类误操作必须在这里挡住，而不是让 Dock 显示一块空白。
/// </summary>
public class DockIconOverrideTests
{
    private static string TempFile(string extension, int bytes = 16)
    {
        var path = Path.Combine(Path.GetTempPath(), $"beautiful-icon-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void 合法图片返回绝对路径()
    {
        var path = TempFile(".png");
        try
        {
            var result = DockIconOverride.Validate(path, out var reason);
            Assert.NotNull(result);
            Assert.Equal("", reason);
            Assert.True(Path.IsPathRooted(result));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(".gif")]
    [InlineData(".mp4")]
    [InlineData(".exe")]
    public void 不支持的扩展名被拒绝(string extension)
    {
        var path = TempFile(extension);
        try
        {
            Assert.Null(DockIconOverride.Validate(path, out var reason));
            Assert.Contains("不支持的格式", reason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 不存在空文件与超限都被拒绝()
    {
        Assert.Null(DockIconOverride.Validate(@"C:/definitely/missing.png", out var missing));
        Assert.Contains("不存在", missing);

        var empty = TempFile(".png", 0);
        try
        {
            Assert.Null(DockIconOverride.Validate(empty, out var emptyReason));
            Assert.Contains("空文件", emptyReason);
        }
        finally
        {
            File.Delete(empty);
        }

        Assert.Null(DockIconOverride.Validate("", out var blank));
        Assert.Contains("没有选择", blank);
    }
}
