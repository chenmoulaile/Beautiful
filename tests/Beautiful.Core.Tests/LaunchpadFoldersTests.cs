using Beautiful.Core.Launchpad;
using Xunit;

/// <summary>
/// Launchpad 文件夹操作纯函数测试：创建、加成员、减成员 disslove、页内拖放合并。
/// </summary>
public class LaunchpadFoldersTests
{
    private const string AppA = "calc";
    private const string AppB = "notepad";
    private const string AppC = "mspaint";

    // ---------- Create ----------

    [Fact]
    public void 两个应用_创建文件夹_名为_target_成员有序()
    {
        var folder = LaunchpadFolders.Create(AppA, AppB);
        Assert.Equal(AppA, folder.Name);
        Assert.Equal(new[] { AppA, AppB }, folder.MemberIds.ToArray());
    }

    [Fact]
    public void 显式命名_覆盖默认名()
    {
        var folder = LaunchpadFolders.Create(AppA, AppB, "实用工具");
        Assert.Equal("实用工具", folder.Name);
        Assert.Equal(new[] { AppA, AppB }, folder.MemberIds.ToArray());
    }

    // ---------- AddMember ----------

    [Fact]
    public void 加新成员_追加到末尾()
    {
        var folder = LaunchpadFolders.Create(AppA, AppB);
        var updated = LaunchpadFolders.AddMember(folder, AppC);
        Assert.Equal(new[] { AppA, AppB, AppC }, updated.MemberIds.ToArray());
    }

    [Fact]
    public void 加已有成员_去重_原样返回()
    {
        var folder = LaunchpadFolders.Create(AppA, AppB);
        var updated = LaunchpadFolders.AddMember(folder, AppA);
        Assert.Same(folder, updated);
        Assert.Equal(new[] { AppA, AppB }, updated.MemberIds.ToArray());
    }

    // ---------- RemoveMember ----------

    [Fact]
    public void 从_2_人文件夹移除_1_人_解散为_App()
    {
        var folder = LaunchpadFolders.Create(AppA, AppB);
        var (result, dissolved) = LaunchpadFolders.RemoveMember(folder, AppA);
        Assert.True(dissolved);
        Assert.IsType<LaunchpadItem.App>(result);
        Assert.Equal(AppB, ((LaunchpadItem.App)result).Id);
    }

    [Fact]
    public void 从_3_人文件夹移除_1_人_返回缩减文件夹()
    {
        var folder = new LaunchpadItem.Folder("工具", new[] { AppA, AppB, AppC });
        var (result, dissolved) = LaunchpadFolders.RemoveMember(folder, AppB);
        Assert.False(dissolved);
        Assert.IsType<LaunchpadItem.Folder>(result);
        Assert.Equal(new[] { AppA, AppC }, ((LaunchpadItem.Folder)result).MemberIds.ToArray());
    }

    [Fact]
    public void 移除不存在的成员_返回原文件夹()
    {
        var folder = LaunchpadFolders.Create(AppA, AppB);
        var (result, dissolved) = LaunchpadFolders.RemoveMember(folder, AppC);
        Assert.False(dissolved);
        Assert.Same(folder, result);
    }

    // ---------- Merge ----------

    [Fact]
    public void 合并两个文件夹_成员去重_顺序保持()
    {
        var a = new LaunchpadItem.Folder("A", new[] { AppA, AppB });
        var b = new LaunchpadItem.Folder("B", new[] { AppB, AppC });
        var merged = LaunchpadFolders.Merge(a, b);
        Assert.Equal("A", merged.Name);
        Assert.Equal(new[] { AppA, AppB, AppC }, merged.MemberIds.ToArray());
    }

    // ---------- MergePageOnDrop ----------

    private static LaunchpadItem?[] TwoAppsPage() => new LaunchpadItem?[]
    {
        new LaunchpadItem.App(AppA),
        new LaunchpadItem.App(AppB),
    };

    [Fact]
    public void App_拖_App_创建文件夹_源槽清空()
    {
        var page = TwoAppsPage();
        var result = LaunchpadFolders.MergePageOnDrop(page, sourceSlot: 0, targetSlot: 1);

        Assert.Null(result[0]);
        var folder = Assert.IsType<LaunchpadItem.Folder>(result[1]);
        Assert.Equal(new[] { AppB, AppA }, folder.MemberIds.ToArray());
    }

    [Fact]
    public void App_拖_已有文件夹_加进文件夹_源槽清空()
    {
        var folder = LaunchpadFolders.Create(AppA, AppB);
        var page = new LaunchpadItem?[]
        {
            new LaunchpadItem.App(AppC),
            folder,
        };
        var result = LaunchpadFolders.MergePageOnDrop(page, 0, 1);
        Assert.Null(result[0]);
        var updated = Assert.IsType<LaunchpadItem.Folder>(result[1]);
        Assert.Equal(new[] { AppA, AppB, AppC }, updated.MemberIds.ToArray());
    }

    [Fact]
    public void App_拖_空位_直接移动()
    {
        var page = new LaunchpadItem?[]
        {
            new LaunchpadItem.App(AppA),
            null,
        };
        var result = LaunchpadFolders.MergePageOnDrop(page, 0, 1);
        Assert.Null(result[0]);
        Assert.Equal(new LaunchpadItem.App(AppA), result[1]);
    }

    [Fact]
    public void 拖到自己_原样返回()
    {
        var page = TwoAppsPage();
        var result = LaunchpadFolders.MergePageOnDrop(page, 0, 0);
        Assert.Equal(page, result);
    }

    [Fact]
    public void 拖空槽_原样返回()
    {
        var page = new LaunchpadItem?[] { null, new LaunchpadItem.App(AppB) };
        var result = LaunchpadFolders.MergePageOnDrop(page, 0, 1);
        Assert.Equal(page, result);
    }

    [Fact]
    public void 拖同一个应用_ID_到自己名下_是_No_op()
    {
        var page = new LaunchpadItem?[]
        {
            new LaunchpadItem.App(AppA),
            new LaunchpadItem.App(AppA),
        };
        var result = LaunchpadFolders.MergePageOnDrop(page, 0, 1);
        Assert.Equal(page, result);
    }

    [Fact]
    public void Folder_拖_Folder_合并成员()
    {
        var page = new LaunchpadItem?[]
        {
            new LaunchpadItem.Folder("F1", new[] { AppA, AppB }),
            new LaunchpadItem.Folder("F2", new[] { AppC }),
        };
        var result = LaunchpadFolders.MergePageOnDrop(page, 0, 1);
        Assert.Null(result[0]);
        var merged = Assert.IsType<LaunchpadItem.Folder>(result[1]);
        Assert.Equal(new[] { AppC, AppA, AppB }, merged.MemberIds.ToArray());
    }

    [Fact]
    public void Folder_拖_App_交换位置()
    {
        var page = new LaunchpadItem?[]
        {
            new LaunchpadItem.Folder("F", new[] { AppA, AppB }),
            new LaunchpadItem.App(AppC),
        };
        var result = LaunchpadFolders.MergePageOnDrop(page, 0, 1);
        Assert.Equal(new LaunchpadItem.App(AppC), result[0]);
        Assert.IsType<LaunchpadItem.Folder>(result[1]);
    }

    [Fact]
    public void 槽位越界_抛出()
    {
        var page = TwoAppsPage();
        Assert.Throws<ArgumentOutOfRangeException>(() => LaunchpadFolders.MergePageOnDrop(page, 0, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => LaunchpadFolders.MergePageOnDrop(page, 5, 0));
    }

    [Fact]
    public void 空页_返回_空数组()
    {
        var result = LaunchpadFolders.MergePageOnDrop([], 0, 0);
        Assert.Empty(result);
    }
}
