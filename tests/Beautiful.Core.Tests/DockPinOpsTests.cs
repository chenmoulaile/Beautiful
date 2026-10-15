using Beautiful.Core.Dock;
using Xunit;

public class DockPinOpsTests
{
    // 假解析器：把 .lnk 映射到其"目标 exe"，其余原样。用它验证"同一目标的不同写法"折叠成一项。
    private static readonly Func<string, string> LnkToExe = p =>
        p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? p[..^4] + ".exe" : p;

    // ---------- 结构自愈解析 ----------

    [Fact]
    public void TryParsePaths_合法数组_逐项返回且保序()
    {
        Assert.True(DockPinOps.TryParsePaths("""["a","b","c"]""", out var raw));
        Assert.Equal(new[] { "a", "b", "c" }, raw);
    }

    [Fact]
    public void TryParsePaths_损坏JSON_返回false()
    {
        Assert.False(DockPinOps.TryParsePaths("{ 这不是 JSON !!!", out var raw));
        Assert.Empty(raw);
    }

    [Fact]
    public void TryParsePaths_null字面量_视为损坏()
    {
        Assert.False(DockPinOps.TryParsePaths("null", out _));
    }

    [Fact]
    public void TryParsePaths_非数组_视为损坏()
    {
        Assert.False(DockPinOps.TryParsePaths("""{"a":1}""", out _));
    }

    // ---------- 归一去重 ----------

    [Fact]
    public void Normalize_丢空白并按原路径忽略大小写去重()
    {
        var r = DockPinOps.Normalize(new[] { "C:/a.exe", "  ", "C:/A.exe", "", "C:/b.exe" });
        Assert.Equal(new[] { "C:/a.exe", "C:/b.exe" }, r);
    }

    [Fact]
    public void Normalize_按解析目标去重_保留首次出现的原始写法()
    {
        // everything.lnk 与 everything.exe 是同一个目标：只留第一条（.lnk），第二条折叠掉
        var r = DockPinOps.Normalize(new[] { "c:/m/everything.lnk", "c:/m/everything.exe", "c:/x.exe" }, LnkToExe);
        Assert.Equal(new[] { "c:/m/everything.lnk", "c:/x.exe" }, r);
    }

    [Fact]
    public void Normalize_解析不出目标_原样保留不误删()
    {
        // 解析器对坏项返回空串：不能把它当成"空"直接丢，应保留原始路径
        var emptyResolver = new Func<string, string>(_ => "");
        var r = DockPinOps.Normalize(new[] { "c:/broken.lnk", "c:/ok.exe" }, emptyResolver);
        Assert.Equal(new[] { "c:/broken.lnk", "c:/ok.exe" }, r);
    }

    [Fact]
    public void Normalize_解析器抛异常_退回原串不崩()
    {
        var throwing = new Func<string, string>(p => p.Contains("bad") ? throw new IOException() : p);
        var r = DockPinOps.Normalize(new[] { "c:/bad.lnk", "c:/ok.exe" }, throwing);
        Assert.Equal(new[] { "c:/bad.lnk", "c:/ok.exe" }, r);
    }

    // ---------- 下标查找 ----------

    [Fact]
    public void IndexOfTarget_忽略大小写与斜杠差异命中_未命中返回负一()
    {
        var list = new List<string> { "C:/Win/Notepad.exe", "c:/a.exe" };
        Assert.Equal(0, DockPinOps.IndexOfTarget(list, "c:/win/notepad.exe"));
        Assert.Equal(-1, DockPinOps.IndexOfTarget(list, "c:/missing.exe"));
    }

    // ---------- 插入 ----------

    [Fact]
    public void Insert_槽位越界钳到两端()
    {
        var list = new List<string> { "a", "b" };
        Assert.Equal(0, DockPinOps.Insert(list, "x", -5, null, out _));
        Assert.Equal("x", list[0]);
        Assert.Equal(3, DockPinOps.Insert(list, "y", 99, null, out _));
        Assert.Equal("y", list[^1]);
    }

    [Fact]
    public void Insert_新项_added为真并落位()
    {
        var list = new List<string> { "a", "b", "c" };
        var idx = DockPinOps.Insert(list, "z", 1, null, out var added);
        Assert.True(added);
        Assert.Equal(1, idx);
        Assert.Equal(new[] { "a", "z", "b", "c" }, list);
    }

    [Fact]
    public void Insert_重复目标_added为假且返回既有下标_表不变()
    {
        var list = new List<string> { "m/everything.lnk", "b" };
        // 想插 everything.exe（同一目标）：不新增，返回 .lnk 的下标 0，让调用方决定是否 Move
        var idx = DockPinOps.Insert(list, "m/everything.exe", 1, LnkToExe, out var added);
        Assert.False(added);
        Assert.Equal(0, idx);
        Assert.Equal(new[] { "m/everything.lnk", "b" }, list);
    }

    // ---------- 移动 ----------

    [Fact]
    public void MoveToSlot_向后移_目标索引左移一格()
    {
        // 把 0 移到"移除前槽 3"：移除后只剩 3 项，槽 3 需要减一 → 落到末尾之前?  期望 a 到 c 之后、d 之前
        var list = new List<string> { "a", "b", "c", "d" };
        Assert.True(DockPinOps.MoveToSlot(list, 0, 3));
        Assert.Equal(new[] { "b", "c", "a", "d" }, list);
    }

    [Fact]
    public void MoveToSlot_向前移_目标索引不变()
    {
        var list = new List<string> { "a", "b", "c", "d" };
        Assert.True(DockPinOps.MoveToSlot(list, 3, 1));
        Assert.Equal(new[] { "a", "d", "b", "c" }, list);
    }

    [Fact]
    public void MoveToSlot_槽位越界钳到端点()
    {
        var list = new List<string> { "a", "b", "c" };
        Assert.True(DockPinOps.MoveToSlot(list, 0, 99));   // 移到末尾
        Assert.Equal(new[] { "b", "c", "a" }, list);
        Assert.True(DockPinOps.MoveToSlot(list, 2, -3));    // 移到首位
        Assert.Equal(new[] { "a", "b", "c" }, list);
    }

    [Fact]
    public void MoveToSlot_原地或相邻右侧_无变化()
    {
        var list = new List<string> { "a", "b", "c" };
        Assert.True(DockPinOps.MoveToSlot(list, 1, 1));   // 槽=自身：移除后插回原位
        Assert.Equal(new[] { "a", "b", "c" }, list);
        Assert.True(DockPinOps.MoveToSlot(list, 1, 2));   // from<slot 抵消 → 仍原位
        Assert.Equal(new[] { "a", "b", "c" }, list);
    }

    [Fact]
    public void MoveToSlot_from越界_返回false且表不变()
    {
        var list = new List<string> { "a", "b" };
        Assert.False(DockPinOps.MoveToSlot(list, 5, 0));
        Assert.False(DockPinOps.MoveToSlot(list, -1, 0));
        Assert.Equal(new[] { "a", "b" }, list);
    }

    [Fact]
    public void MoveToSlot_往返可逆_先移后移回得原表()
    {
        var list = new List<string> { "a", "b", "c", "d", "e" };
        DockPinOps.MoveToSlot(list, 0, 4);
        DockPinOps.MoveToSlot(list, 3, 0);
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, list);
    }

    // ---------- 移除 ----------

    [Fact]
    public void Remove_命中移除返回true_未命中false()
    {
        var list = new List<string> { "a", "b" };
        Assert.True(DockPinOps.Remove(list, "A"));
        Assert.Equal(new[] { "b" }, list);
        Assert.False(DockPinOps.Remove(list, "zzz"));
    }

    [Fact]
    public void Remove_按目标移除_lnk与exe同源互删()
    {
        var list = new List<string> { "m/everything.lnk", "b" };
        // 用目标 exe 去取消固定 .lnk 项（拖出/右键取消固定要能删掉快捷方式那条）
        Assert.True(DockPinOps.Remove(list, "m/everything.exe", LnkToExe));
        Assert.Equal(new[] { "b" }, list);
    }
}
