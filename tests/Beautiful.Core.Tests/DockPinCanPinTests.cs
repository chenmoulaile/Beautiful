using Beautiful.Core.Dock;
using Xunit;

/// <summary>v4 Phase 3-T3：Dock 可固定来源（文件夹堆栈 / exe / lnk）的纯函数取证。</summary>
public class DockPinCanPinTests
{
    [Fact]
    public void 文件夹_exe_lnk可固定_其它文件与不存在拒绝()
    {
        var dir = Directory.CreateTempSubdirectory("beautiful-pin-");
        try
        {
            var folder = Path.Combine(dir.FullName, "Downloads");
            Directory.CreateDirectory(folder);
            Assert.True(DockPinOps.CanPin(folder, out _));

            var exe = Path.Combine(dir.FullName, "app.exe");
            File.WriteAllText(exe, "");
            Assert.True(DockPinOps.CanPin(exe, out _));

            var lnk = Path.Combine(dir.FullName, "app.lnk");
            File.WriteAllText(lnk, "");
            Assert.True(DockPinOps.CanPin(lnk, out _));

            var txt = Path.Combine(dir.FullName, "note.txt");
            File.WriteAllText(txt, "");
            Assert.False(DockPinOps.CanPin(txt, out var reason));
            Assert.Contains("仅支持固定文件夹", reason);

            Assert.False(DockPinOps.CanPin(Path.Combine(dir.FullName, "missing.exe"), out var missing));
            Assert.Contains("不存在", missing);
            Assert.False(DockPinOps.CanPin("  ", out var empty));
            Assert.Equal("空路径", empty);
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch { /* 测试清理尽力而为 */ }
        }
    }
}
