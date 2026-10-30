using System.Linq;
using Beautiful.Core.Launcher;
using Xunit;

/// <summary>
/// Launchpad 文件夹规则测试（Phase 5 收尾）。
/// <para>
/// 拖拽合并这类交互在真机上只能靠手点，语义必须在这里钉死：上限 24、同文件夹去重、
/// 合并保序、移空即删、名字归一。UI 只做"把结果画出来"。
/// </para>
/// </summary>
public class LaunchpadFolderRulesTests
{
    private static LaunchpadFolder Folder(params string[] ids)
        => new("f1", "开发工具", ids);

    [Fact]
    public void 默认名取应用名并归一()
    {
        Assert.Equal("微信 的文件夹", LaunchpadFolderRules.DefaultName("微信"));
        Assert.Equal("应用 的文件夹", LaunchpadFolderRules.DefaultName("   "));
    }

    [Fact]
    public void 名字去控制字符折叠空白并截断()
    {
        Assert.Equal("a b", LaunchpadFolderRules.NormalizeName(" a\t\u0001b "));
        Assert.Equal("文件夹", LaunchpadFolderRules.NormalizeName(""));
        Assert.True(LaunchpadFolderRules.NormalizeName(new string('x', 100)).Length <= LaunchpadFolderRules.MaxNameChars);
    }

    [Fact]
    public void 加入应用成功且不允许重复与空id()
    {
        var folder = Folder("a");

        var added = LaunchpadFolderRules.Add(folder, "b", out var reason);
        Assert.NotNull(added);
        Assert.Equal(new[] { "a", "b" }, added!.AppIds);
        Assert.Equal("", reason);

        Assert.Null(LaunchpadFolderRules.Add(folder, "a", out var duplicate));
        Assert.Contains("已在此文件夹", duplicate);

        Assert.Null(LaunchpadFolderRules.Add(folder, " ", out var empty));
        Assert.Contains("为空", empty);
    }

    [Fact]
    public void 满二十四格后拒绝再加入()
    {
        var ids = Enumerable.Range(0, LaunchpadFolderRules.MaxAppsPerFolder).Select(i => $"app{i}").ToArray();
        var folder = Folder(ids);

        Assert.Null(LaunchpadFolderRules.Add(folder, "extra", out var reason));
        Assert.Contains("已满", reason);
    }

    [Fact]
    public void 移除后为空则返回null表示删掉文件夹()
    {
        Assert.Null(LaunchpadFolderRules.Remove(Folder("a"), "a"));
        Assert.Equal(new[] { "a" }, LaunchpadFolderRules.Remove(Folder("a", "b"), "b")!.AppIds);
    }

    [Fact]
    public void 合并保序去重且超限拒绝()
    {
        var merged = LaunchpadFolderRules.Merge(Folder("a", "b"), Folder("b", "c"), out var reason);
        Assert.NotNull(merged);
        Assert.Equal(new[] { "a", "b", "c" }, merged!.AppIds);
        Assert.Equal("", reason);

        var full = Folder(Enumerable.Range(0, 24).Select(i => $"x{i}").ToArray());
        Assert.Null(LaunchpadFolderRules.Merge(full, Folder("y"), out var over));
        Assert.Contains("超过", over);
    }
}
