using Beautiful.Core.Dock;
using Xunit;

/// <summary>
/// 回收站槽位满/空判定与图标键（纯函数）：图标口径与菜单口径分开（失败兜底方向相反）、
/// 满态候选键先 full 后 empty（图集缺图回退，绝不空白上屏）。判定不碰 Win32，
/// 读数（hr / i64NumItems）全部手工构造。
/// </summary>
public class RecycleBinSlotTests
{
    // ---------- 图标口径：查询成功（hr == 0）按条目数判满 ----------

    [Theory]
    [InlineData(0, false)]    // 0 条 = 空（历史起步态）
    [InlineData(1, true)]     // 有条目即满
    [InlineData(42, true)]
    [InlineData(-1, false)]   // 异常读数（负数）按空处理，不凭空"变满"
    public void 判满_按条目数(long numItems, bool expected)
        => Assert.Equal(expected, RecycleBinSlot.IsFull(numItems));

    // ---------- 菜单口径：失败保守当作"有可清"（历史行为逐位一致） ----------

    [Theory]
    [InlineData(0u, 0, false)]           // 成功且 0 条 → 无可清（菜单置灰"回收站已是空的"）
    [InlineData(0u, 3, true)]            // 成功且有条目 → 可清
    [InlineData(0x80070005u, 0, true)]   // 查询失败（E_ACCESSDENIED）→ 保守当作有可清
    [InlineData(1u, 7, true)]            // 任意非 0 hr 同上
    public void 菜单口径_查询失败保守当作有可清(uint hr, long numItems, bool expected)
        => Assert.Equal(expected, RecycleBinSlot.MenuAssumesItems(hr, numItems));

    [Fact]
    public void 菜单口径_失败兜底与图标口径方向相反但同源()
    {
        // 同一份读数（成功 + 0 条）：菜单判"无可清"，图标判"空"——两边一致；
        // （查询失败时菜单当作有可清，图标轮询保持上一次状态——那是 App 侧的职责，不在纯函数里。）
        Assert.False(RecycleBinSlot.MenuAssumesItems(0, 0));
        Assert.False(RecycleBinSlot.IsFull(0));
    }

    // ---------- 图标键 ----------

    [Fact]
    public void 图标键_满空两态()
    {
        Assert.Equal("trash_full", RecycleBinSlot.IconKeyFor(true));
        Assert.Equal("trash_empty", RecycleBinSlot.IconKeyFor(false));
    }

    [Fact]
    public void 候选键链_满态先full后empty_绝不空白()
    {
        // 满态：trash_full.png 缺失时回退 trash_empty.png（MacIconCatalog 逐键试读）
        Assert.Equal(new[] { "trash_full", "trash_empty" }, RecycleBinSlot.IconKeyCandidates(true));
        Assert.Equal(new[] { "trash_empty" }, RecycleBinSlot.IconKeyCandidates(false));
    }
}
