using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Beautiful.App.Interop;
using static Beautiful.App.Interop.NativeMethods;
using Brushes = System.Windows.Media.Brushes;   // UseWindowsForms 隐式全局 using 与 System.Drawing.Brushes 冲突
using Color = System.Windows.Media.Color;        // 同上，System.Drawing.Color

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
/// 【玻璃感从哪来】两条路，都不碰 DWM 材质 API：
/// <list type="bullet">
/// <item><description>瞬态表面（切换器、菜单弹层、媒体面板）——显示前抓屏自绘模糊，见
/// <see cref="ScreenBlurBackdrop"/>。透明负责"形状"，抓屏负责"材质"。</description></item>
/// <item><description>常驻表面（顶栏、Dock）——自绘半透明 tint（主题 <c>ThemeDockBackground</c> /
/// <c>ThemeBackdropTint</c> + 顶部内高光 + 1px 亮边）作为**唯一可见底**，再叠加一次 best-effort 的旧
/// <c>SetWindowCompositionAttribute(ACCENT_ENABLE_ACRYLICBLURBEHIND)</c>（见
/// <see cref="ApplyTintFallback"/>）。</description></item>
/// </list>
/// </para>
/// <para>
/// 【best-effort 的取舍（README 引用这段）】常驻表面不能做实时抓屏：重抓会把窗口自己已经画好的像素
/// 一起抓进背板（鬼影），而为了抓屏先隐藏一帧就是肉眼可见的闪烁。因此它们只求系统侧那层模糊作为
/// 锦上添花（Dock 自 v1 起即此形态，ModernFlyouts / MyDockFinder 同思路）：系统认账 → 背后是真模糊；
/// 系统不认账（本 VM、部分 Win11 配置）→ 看到的仍是我们自绘的 tint 底。两种结果都不会是黑，
/// 返回值只落日志，绝不作为渲染前提。
/// </para>
/// </summary>
public class AcrylicWindow : Window
{
    public AcrylicWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;   // 逐像素 alpha 的唯一保证（见类型注释）；派生类的 XAML 只能保持 true，不得改回
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
    }

    /// <summary>
    /// 常驻表面的底色策略：可见底由 XAML 自绘（<c>ThemeDockBackground</c> 等 DynamicResource，换主题即变色），
    /// 本方法只额外请求一次旧亚克力模糊作为 best-effort 增强；<paramref name="accentEnabled"/>=false
    /// 时把该增强关掉（Linux 主题禁模糊 = 纯实底形态，与"关掉玻璃"是同一常规分支，不是异常）。
    /// </summary>
    /// <param name="tint">主题 AcrylicTint（含 alpha）：只有旧亚克力路径会把它当渐变底用（GradientColor）。</param>
    /// <param name="accentEnabled">是否请求系统侧模糊。</param>
    protected void ApplyTintFallback(Color tint, bool accentEnabled = true)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var ok = TryApplyAccent(hwnd, tint, accentEnabled ? AccentState.AcrylicBlurBehind : AccentState.Disabled);
        Logger.Info($"常驻表面底色 hwnd=0x{hwnd.ToInt64():X} 自绘tint=必画 " +
                    $"best-effort旧亚克力={(accentEnabled ? (ok ? "已请求" : "未生效") : "已关闭")}" +
                    "（不生效时观感=纯自绘半透明底，绝不落黑）");
    }

    /// <summary>
    /// 旧 <c>SetWindowCompositionAttribute</c> 亚克力（分层窗口上唯一还可能出现的系统侧模糊）。
    /// 成功返回 true；任何异常都吞掉并按"没有增强"处理——底色早已由内容画好，这里失败不改变可读性。
    /// </summary>
    private static bool TryApplyAccent(IntPtr hwnd, Color tint, AccentState state)
    {
        var accent = new ACCENT_POLICY
        {
            AccentState = state,
            AccentFlags = 2,
            GradientColor = ToAbgr(tint),
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
            Logger.Warn($"旧亚克力增强请求失败（不影响自绘底色）: {ex.Message}");
            return false;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }
}
