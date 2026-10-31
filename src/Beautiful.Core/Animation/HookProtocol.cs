using System;

namespace Beautiful.Core.Animation;

/// <summary>
/// Beautiful.Hook.dll（native/hook/beautiful_hook.c，MinGW 交叉编译）与宿主端
/// <c>Beautiful.App.Animation.MinimizeHookClient</c> 的共享内存协议 v2 布局。
/// <para>
/// 【为什么在 Core】C 侧结构体与本类必须逐字节一致；把偏移量收进一个可单测的常量类，
/// C#/C 两边都对着同一份"图纸"写，钉子测试（HookProtocolTests）防止任何一侧漂移。
/// v1 的教训：偏移量散落在宿主端代码里，C 侧改动无法被任何测试看见。
/// </para>
/// <para>
/// 【x64 对齐】所有 64 位字段（HostHwnd/SuppressHwnd/Hwnd）都在 8 字节边界上；
/// 共享头与槽头都是 8 的倍数字节，尾部补齐到 48（struct 自然对齐后的尺寸）。
/// </para>
/// <para>
/// 【v2 相对 v1】① 共享区/事件命名空间从 .v1 升到 .v2（防旧版本残留节被新代码误认）；
/// ② 槽头 24→48 字节：新增 Kind（MIN/DESTROY/SHOW）、Pid、窗口矩形（抓帧瞬间的物理像素矩形）；
/// ③ Flags 新增 bit1 = 关闭动画开关（宿主写，钩子侧 HCBT_DESTROYWND 拦截前检查）。
/// </para>
/// </summary>
public static class HookProtocol
{
    /// <summary>"BHK2"（v1 是 "BHK1"）：魔数不一致 = 残留旧节，宿主/注入侧都判废。</summary>
    public const uint Magic = 0x32484B42u;

    /// <summary>协议版本（BH_Version 返回值）。</summary>
    public const uint Version = 2;

    // ---------- BH_SHARED（共享头，48 字节） ----------

    /// <summary>BH_SHARED 尺寸（含尾部对齐）。</summary>
    public const int SharedHeaderBytes = 48;

    public const int OffsetMagic = 0;
    public const int OffsetVersion = 4;
    public const int OffsetHostHwnd = 8;
    public const int OffsetNotifyMsg = 16;
    public const int OffsetFlags = 20;
    public const int OffsetSuppressHwnd = 24;
    public const int OffsetSeq = 32;
    public const int OffsetSlotBytes = 36;
    public const int OffsetSlotCount = 40;

    /// <summary>Flags bit0：主程序退出中，所有副本立即放行一切（BH_Uninstall 置位）。</summary>
    public const uint FlagShutdown = 0x1u;

    /// <summary>Flags bit1：关闭动画已启用——钩子侧只在置位时拦 HCBT_DESTROYWND 抓帧。</summary>
    public const uint FlagCloseEnabled = 0x2u;

    // ---------- BH_SLOT（槽头，48 字节；像素紧跟其后） ----------

    /// <summary>BH_SLOT 尺寸（ReadySeq..Bottom，含对齐补齐）。</summary>
    public const int SlotHeaderBytes = 48;

    public const int SlotOffsetReadySeq = 0;
    public const int SlotOffsetWidth = 4;
    public const int SlotOffsetHeight = 8;
    public const int SlotOffsetStride = 12;
    public const int SlotOffsetHwnd = 16;
    public const int SlotOffsetKind = 24;
    public const int SlotOffsetPid = 28;
    /// <summary>四个 INT32：Left/Top/Right/Bottom（抓帧瞬间的 GetWindowRect，物理像素）。</summary>
    public const int SlotOffsetRectLeft = 32;
    public const int SlotOffsetRectTop = 36;
    public const int SlotOffsetRectRight = 40;
    public const int SlotOffsetRectBottom = 44;

    /// <summary>钩子交帧的种类：原生最小化前拦下的帧。</summary>
    public const uint KindMinimize = 1;

    /// <summary>窗口销毁前抓下的最后一帧（不阻断销毁，只为关闭动画供帧）。</summary>
    public const uint KindDestroy = 2;

    /// <summary>窗口显示前已就地 Cloak（槽内无像素，宿主自行抓帧后播入场动画再 Uncloak）。</summary>
    public const uint KindShow = 3;

    // ---------- 几何上限（与 C 侧 BH_MAX_* 一致） ----------

    public const int MaxWidth = 2560;
    public const int MaxHeight = 1440;
    public const int MinWidth = 120;
    public const int MinHeight = 120;

    /// <summary>槽数（双槽乒乓：注入侧写第 N 槽时宿主还在读第 N-1 槽）。</summary>
    public const int SlotCount = 2;

    /// <summary>单槽像素字节数（上限矩形，槽按最大尺寸预留）。</summary>
    public static int SlotBytes => MaxWidth * MaxHeight * 4;

    /// <summary>共享节总字节数：头 + 2 ×（槽头 + 像素区）。</summary>
    public static int SectionBytes => SharedHeaderBytes + SlotCount * (SlotHeaderBytes + SlotBytes);

    /// <summary>序号所在槽的基址（槽号 = 序号奇偶，两侧一致）。</summary>
    public static int SlotBase(uint seq) => SharedHeaderBytes + (int)(seq & 1) * (SlotHeaderBytes + SlotBytes);
}
