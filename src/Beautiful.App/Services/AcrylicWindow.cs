using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Beautiful.App.Interop;
using static Beautiful.App.Interop.NativeMethods;
using Brushes = System.Windows.Media.Brushes;   // UseWindowsForms 隐式全局 using 与 System.Drawing.Brushes 冲突

namespace Beautiful.App.Services;

/// <summary>
/// 分层（逐像素透明）窗口基类：本项目所有需要透明或非矩形形状的表面（顶栏 / Dock / 媒体面板）的共同形态。
/// <para>
/// 【为什么"分层"是唯一选择（v3 §1.B 裁定）】WPF 里"客户端逐像素 alpha 真的生效"只有
/// <see cref="Window.AllowsTransparency"/> = true 一条路（内部即 <c>WS_EX_LAYERED</c>），它在 Win10 与
/// Win11 各版本上行为一致，并且本机就能截屏验证。v2 依赖的"非分层窗口 + DWM 材质 + 半透明内容"已被
/// 用户真机证伪：半透明卡底下的客户端像素不会被合成，结果就是一块带硬边的黑矩形。
/// </para>
/// <para>
/// 【玻璃感从哪来（v3.1 收口）】两条路，**两条都不碰 DWM/旧亚克力的"系统自己画底"API**：
/// <list type="bullet">
/// <item><description>瞬态表面（切换器、菜单弹层、媒体面板）——显示前抓屏自绘模糊，见
/// <see cref="ScreenBlurBackdrop"/>。透明负责"形状"，抓屏负责"材质"。</description></item>
/// <item><description>常驻表面（顶栏、Dock）——自绘半透明 tint（主题 <c>ThemeDockBackground</c> +
/// 顶部内高光 + 1px 亮边）就是**唯一可见底**。</description></item>
/// </list>
/// </para>
/// <para>
/// 【v3.1 为什么把旧亚克力增强删干净（缺陷 1）】v3.0.0 在这里另发一次
/// <c>SetWindowCompositionAttribute(ACCENT_ENABLE_ACRYLICBLURBEHIND)</c> 当作锦上添花。真机（Win11
/// 24H2/25H2）上它已经不模糊，而是把 <c>GradientColor</c>（我们传的主题 tint）**平铺满整个窗口矩形**：
/// Dock 的窗口矩形 = 全显示器宽 ×（面板 + 放大余量），于是屏幕底边出现一条全宽深色带，遮住后面窗口的
/// 下一行——正是用户报的"dock 栏还是有一段是黑的"。本 VM 不合成，画不出这条带，所以 v3 全程没发现。
/// 现在常驻表面的观感**只**由 XAML 决定：条带里除面板以外的像素保持真透明（<c>Background={x:Null}</c>
/// 的分层窗口未绘制区不参与合成也不参与命中测试），屏上不可能再出现"窗口矩形被系统涂满"。
/// </para>
/// <para>
/// 【剩下的一次系统调用是清理，不是增强】见 <see cref="ClearLegacyAccent"/>。
/// </para>
/// </summary>
public class AcrylicWindow : Window
{
    private bool _legacyAccentCleared;

    public AcrylicWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;   // 逐像素 alpha 的唯一保证（见类型注释）；派生类的 XAML 只能保持 true，不得改回
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
    }

    /// <summary>
    /// 常驻表面（顶栏 / Dock）的底色落地：可见底恒由派生类 XAML 自绘（<c>ThemeDockBackground</c> 等
    /// DynamicResource，换主题即变色），本方法只做一件事——把 <b>v3.0.0 遗留在本窗口上的旧亚克力状态关掉</b>。
    /// <para>
    /// 【为什么这一句不能一起删】accent 状态是挂在窗口上的进程外可见效果：跑过 v3.0.0 的机器，它的顶栏与
    /// Dock 窗口已被写入 <c>AcrylicBlurBehind</c>，升级到 v3.1 之后没有任何代码会再去碰它，那块全宽填充
    /// 就会一直留在屏上。所以每个窗口第一次落主题时显式写一次 <see cref="AccentState.Disabled"/>
    /// （=0）清除；这是本进程唯一一次、也是唯一允许的一次 <c>SetWindowCompositionAttribute</c> 调用。
    /// </para>
    /// <para>
    /// 【幂等与频次】Disabled 是"关掉"，重复写无害，但为了日志可读，每个窗口实例只写一次
    /// （<see cref="_legacyAccentCleared"/>）。之后换主题不再调用系统 API，纯 DynamicResource 变色。
    /// </para>
    /// </summary>
    /// <param name="surfaceName">日志里的表面名（"顶栏" / "Dock"），便于真机日志一眼定位是哪一块。</param>
    protected void ClearLegacyAccent(string surfaceName)
    {
        if (_legacyAccentCleared) return;
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (hwnd == IntPtr.Zero) return;   // 句柄未就绪（正常调用点都在 OnSourceInitialized 之后）：下次主题落地再清
        _legacyAccentCleared = true;
        var ok = TryDisableAccent(hwnd);
        // 运行期断言（缺陷 1 的取证点）：日志里出现的 state 只可能是 Disabled(0)，源码里也没有别的可写值
        // （AccentState 枚举只剩 Disabled 一个成员，见 Interop/NativeMethods.cs）。
        Logger.Info($"常驻表面底色 {surfaceName} hwnd=0x{hwnd.ToInt64():X} 可见底=XAML自绘三层 " +
                    $"旧亚克力=清除(state=Disabled(0)，下发={(ok ? "成功" : "系统未受理")}) " +
                    "本进程不写任何非 Disabled 的 accent 状态");
    }

    /// <summary>
    /// 写一次 <c>AccentState.Disabled</c> 关掉遗留亚克力。任何异常都吞掉——这里失败的最坏结果只是
    /// "老用户的那条带这次没清成"，绝不会引入新的可见效果（我们不再请求任何系统材质）。
    /// </summary>
    private static bool TryDisableAccent(IntPtr hwnd)
    {
        var accent = new ACCENT_POLICY
        {
            AccentState = AccentState.Disabled,
            AccentFlags = 0,
            GradientColor = 0,
            AnimationId = 0
        };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<ACCENT_POLICY>());
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = WcaAccentPolicy,
                Data = ptr,
                SizeOfData = Marshal.SizeOf<ACCENT_POLICY>()
            };
            return SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch (Exception ex)
        {
            Logger.Warn($"清除遗留旧亚克力失败（不影响自绘底色）: {ex.Message}");
            return false;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }
}
