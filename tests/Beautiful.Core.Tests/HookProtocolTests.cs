using System;
using Beautiful.Core.Animation;
using Xunit;

/// <summary>
/// 钩子层协议 v2 的钉子测试：C 侧（native/hook/beautiful_hook.c 的 BH_SHARED/BH_SLOT）与
/// C# 侧（Beautiful.App Animation/MinimizeHookClient）共用这一份"图纸"。
/// <para>
/// 【为什么必须钉死】两侧任何一侧的偏移量漂移（加字段/改对齐），另一侧不编译不报错、
/// 运行期才表现为"读不到帧/钩子永不生效"——正是 alpha.16 真机排查里最难啃的那类静默故障。
/// 这里把每个字节的位置与含义钉住：改布局必须连测试一起改，评审一眼可见。
/// </para>
/// </summary>
public class HookProtocolTests
{
    [Fact]
    public void 魔数与版本是BHK2()
    {
        Assert.Equal(0x32484B42u, HookProtocol.Magic);   // "BHK2" 小端
        Assert.Equal(2u, HookProtocol.Version);
    }

    [Fact]
    public void 共享头偏移与C结构体逐字对齐()
    {
        Assert.Equal(48, HookProtocol.SharedHeaderBytes);
        Assert.Equal(0, HookProtocol.OffsetMagic);
        Assert.Equal(4, HookProtocol.OffsetVersion);
        Assert.Equal(8, HookProtocol.OffsetHostHwnd);      // UINT64 @ 8 字节对齐
        Assert.Equal(16, HookProtocol.OffsetNotifyMsg);
        Assert.Equal(20, HookProtocol.OffsetFlags);        // bit0=退出放行 bit1=关闭动画
        Assert.Equal(24, HookProtocol.OffsetSuppressHwnd); // UINT64 @ 8 字节对齐
        Assert.Equal(32, HookProtocol.OffsetSeq);
        Assert.Equal(36, HookProtocol.OffsetSlotBytes);
        Assert.Equal(40, HookProtocol.OffsetSlotCount);
        Assert.Equal(1u, HookProtocol.FlagShutdown);
        Assert.Equal(2u, HookProtocol.FlagCloseEnabled);
    }

    [Fact]
    public void 槽头偏移与C结构体逐字对齐()
    {
        Assert.Equal(48, HookProtocol.SlotHeaderBytes);
        Assert.Equal(0, HookProtocol.SlotOffsetReadySeq);
        Assert.Equal(4, HookProtocol.SlotOffsetWidth);
        Assert.Equal(8, HookProtocol.SlotOffsetHeight);
        Assert.Equal(12, HookProtocol.SlotOffsetStride);
        Assert.Equal(16, HookProtocol.SlotOffsetHwnd);   // UINT64 @ 8 字节对齐
        Assert.Equal(24, HookProtocol.SlotOffsetKind);
        Assert.Equal(28, HookProtocol.SlotOffsetPid);
        Assert.Equal(32, HookProtocol.SlotOffsetRectLeft);
        Assert.Equal(36, HookProtocol.SlotOffsetRectTop);
        Assert.Equal(40, HookProtocol.SlotOffsetRectRight);
        Assert.Equal(44, HookProtocol.SlotOffsetRectBottom);
    }

    [Fact]
    public void 种类与拦截语义一一对应()
    {
        Assert.Equal(1u, HookProtocol.KindMinimize);   // 阻断原生最小化 + 供帧（v1 语义）
        Assert.Equal(2u, HookProtocol.KindDestroy);    // 销毁前供帧，不阻断
        Assert.Equal(3u, HookProtocol.KindShow);       // 首帧上屏前 Cloak，槽内无像素
    }

    [Fact]
    public void 节尺寸与双槽寻址()
    {
        var slotBytes = HookProtocol.MaxWidth * HookProtocol.MaxHeight * 4;
        Assert.Equal(slotBytes, HookProtocol.SlotBytes);
        Assert.Equal(2, HookProtocol.SlotCount);
        Assert.Equal(48 + 2 * (48 + slotBytes), HookProtocol.SectionBytes);

        // 槽号 = 序号奇偶，两侧一致（C 侧 (g_localSeq+1)&1 写、C# 侧 seq&1 读）
        Assert.Equal(HookProtocol.SharedHeaderBytes, HookProtocol.SlotBase(0));
        Assert.Equal(HookProtocol.SharedHeaderBytes, HookProtocol.SlotBase(2));
        Assert.Equal(HookProtocol.SharedHeaderBytes + HookProtocol.SlotHeaderBytes + HookProtocol.SlotBytes,
                     HookProtocol.SlotBase(1));
        Assert.Equal(HookProtocol.SharedHeaderBytes + HookProtocol.SlotHeaderBytes + HookProtocol.SlotBytes,
                     HookProtocol.SlotBase(3));
    }

    [Fact]
    public void 几何上限与抓帧闸门一致()
    {
        Assert.Equal(2560, HookProtocol.MaxWidth);
        Assert.Equal(1440, HookProtocol.MaxHeight);
        Assert.Equal(120, HookProtocol.MinWidth);
        Assert.Equal(120, HookProtocol.MinHeight);
    }
}
