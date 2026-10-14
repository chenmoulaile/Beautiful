using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Beautiful.App.Interop;
using static Beautiful.App.Interop.NativeMethods;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace Beautiful.App.Services;

/// <summary>背板种类（spec：Win11 系统背板替换旧亚克力）。</summary>
public enum BackdropKind { None, Acrylic, Mica }

/// <summary>实际胜出的实现路径。<see cref="None"/> = 显式关闭背板；其余三条为降级链上的三个档位，
/// <see cref="Failed"/> = 三档全失败（此时窗口必须自铺不透明底，见 <see cref="IBackdropAware"/>）。</summary>
public enum BackdropPath
{
    None = 0,
    SystemBackdrop,
    MicaLegacy,
    CompositionAccent,
    Failed
}

/// <summary>
/// 背板内容的底色协商接口：只有 <see cref="BackdropPath.SystemBackdrop"/> /
/// <see cref="BackdropPath.MicaLegacy"/> / <see cref="BackdropPath.CompositionAccent"/> 才真有模糊，
/// 内容才可以半透明让模糊透出；其余路径（含 <see cref="BackdropPath.None"/>，Linux 主题禁模糊）
/// 必须自铺不透明底色——非分层窗口没有逐像素 alpha，半透明底会露出黑色空洞/桌面，可读性反而下降。
/// </summary>
public interface IBackdropAware
{
    void OnBackdropApplied(BackdropPath path);
}

/// <summary>
/// Windows 11 系统背板（Mica/Acrylic）落地：降级链
/// <c>DWMWA_SYSTEMBACKDROP_TYPE(38)</c> → <c>DWMWA_MICA_EFFECT(1029)</c> → 旧
/// <c>SetWindowCompositionAttribute</c>（Win10 亚克力），每档 HRESULT 落日志便于 QA 判定。
/// <para>
/// 【与分层窗口互斥】WPF 唯一的逐像素透明模式是 <see cref="Window.AllowsTransparency"/>，它把窗口变成
/// WS_EX_LAYERED 分层窗口，而 DWM 不在分层窗口后面合成背板（controller 裁定 + 本机实测）。因此背板窗口
/// 必须 <c>AllowsTransparency=false</c>：圆角改由 <c>DWMWA_WINDOW_CORNER_PREFERENCE=ROUND</c> 原生裁切
/// （实测对 <c>WindowStyle=None</c> 的弹层窗口同样生效），内容铺满整个窗口矩形，模糊靠卡片自身的半透明底透出。
/// 需要"两侧点击穿透/任意形状透明"的窗口（DockWindow）只能继续走分层 + 旧亚克力路径，不能用本类。
/// </para>
/// <para>
/// 【必须深色变体】Win11 的 Mica/Acrylic 明暗变体由 <c>DWMWA_USE_IMMERSIVE_DARK_MODE(20)</c> 决定。
/// 本机实测：只写 38=Acrylic 而不写 20 时，浅色变体压在亮色页面上会把整张卡冲成纯白且看不到内容
/// （hr 全 S_OK，纯视觉静默失败）→ 深色 UI 一律先置 20=TRUE 再写背板类型。
/// </para>
/// </summary>
public static class SystemBackdrop
{
    /// <summary>
    /// 应用背板。<paramref name="roundedCorners"/> = 同时请求 DWM 原生圆角（背板窗口的圆角唯一来源）；
    /// <paramref name="darkTint"/> = 请求 <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c>，Win11 的 Mica/Acrylic
    /// 明暗变体由它决定（实测不设则取浅色变体：压在浅色页面上会把整张卡冲成纯白、内容直接看不见）。
    /// 返回胜出的路径；调用方（窗口/内容）据此决定底色是否可半透明。
    /// <para>
    /// 【<paramref name="tint"/> 的实际作用范围（评审 Important#4）】只有档 3（旧
    /// <c>SetWindowCompositionAttribute</c> 亚克力）与"三档全败"的不透明兜底会把 tint 直接当底色用；
    /// 档 1（<c>DWMWA_SYSTEMBACKDROP_TYPE</c>）/ 档 2（未公开 Mica）的模糊取色由系统自己决定
    /// （桌面壁纸 + 系统强调色 + 明暗变体），应用侧写不进颜色。因此主题 tint 在档 1/2 上以内容层
    /// 叠加的形式生效：由内容（<see cref="Controls.FluentMenu"/> / <see cref="MediaWidget.MediaPanel"/>）
    /// 在卡底之上铺一层 <c>ThemeBackdropTint</c>（主题 DynamicResource 画刷），换主题即变色。
    /// 调用方无需（也无法）靠本方法的返回值给档 1/2 上色。
    /// </para>
    /// </summary>
    public static BackdropPath Apply(Window window, Color tint, BackdropKind kind,
                                     bool roundedCorners = true, bool darkTint = true)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();

        if (window.AllowsTransparency)
        {
            // 防御：分层窗口上背板/玻璃层都是静默无效（画不出东西 → 黑卡）。不写任何 DWM 属性，
            // 直接用旧 SetWindowCompositionAttribute（该路径在分层窗口上有效，Dock 即此形态）。
            // kind==None（主题禁模糊，如 Linux）时本就是"关掉玻璃"的常规分支，不是异常 → 走 Info 且
            // 文案不得说"走旧亚克力"（实际是 AccentState.Disabled 关玻璃）。
            var layeredOk = kind == BackdropKind.None
                ? ApplyAccentPolicy(hwnd, Colors.Transparent, AccentState.Disabled)
                : ApplyAccentPolicy(hwnd, tint, AccentState.AcrylicBlurBehind);
            Logger.Info(kind == BackdropKind.None
                ? $"分层窗口跳过 DWM 背板：hwnd=0x{hwnd.ToInt64():X}（主题禁模糊，属预期路径），已关玻璃层 {(layeredOk ? "成功" : "失败")}"
                : $"背板请求落在分层窗口上：hwnd=0x{hwnd.ToInt64():X}（AllowsTransparency=true，DWM 不为其合成背板），" +
                  $"退化为旧亚克力 {(layeredOk ? "成功" : "失败")}");
            return kind == BackdropKind.None ? BackdropPath.None
                 : layeredOk ? BackdropPath.CompositionAccent : BackdropPath.Failed;
        }

        // 玻璃层铺满客户区：背板/旧 accent 都以此为载体（不铺时背板只贴非客户区那圈边框）。
        // 实测：玻璃层本身不会吞掉 WPF 内容（mode3 对照），内容透出靠卡片自身的半透明底。
        var margins = new MARGINS { LeftWidth = -1, RightWidth = -1, TopHeight = -1, BottomHeight = -1 };
        var frameHr = DwmExtendFrameIntoClientArea(hwnd, ref margins);

        // 圆角：背板窗口不能自绘透明外边距，圆角必须由 DWM 裁切（半径 = 系统值，与资源管理器一致）
        int? cornerHr = null; // null = 未尝试（日志打 n/a，避免 -1 被格式化成 0xFFFFFFFF 误读成"失败"）
        if (roundedCorners)
        {
            var round = DwmwcpRound;
            cornerHr = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
        }

        // 深色变体须在写背板类型之前落定（顺序反了时 DWM 会先按浅色合成出一张白卡）；hr 同样留证
        int? darkHr = null;
        if (darkTint && kind != BackdropKind.None)
        {
            var dark = 1;
            darkHr = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        }

        var value = kind switch
        {
            BackdropKind.Acrylic => DwmsbtAcrylic,
            BackdropKind.Mica => DwmsbtMica,
            _ => DwmsbtNone
        };
        var backdropHr = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref value, sizeof(int));

        BackdropPath path;
        int? micaHr = null;
        if (backdropHr == 0 && kind != BackdropKind.None)
        {
            // 22H2+ 真系统背板。
            // 【取证状态 UNVERIFIED，评审 Important#2】hr==0 只证明"属性被接受"，不证明"DWM 真的画了"：
            // 本机（Win11 23H2 开发 VM）已实测两件事——① 卡片内部像素对"弹层后面是什么"完全不敏感
            // （黑白条纹窗 vs 纯黑桌面：卡内均值恒 50.5）→ 至少不是"窗口自身 alpha 直接漏出桌面"；
            // ② 但该 VM 的桌面背景图被禁用（桌面恒 #0C0C0C）、且关闭系统"透明效果"开关后卡内读数也不变
            // → 拿不到"背板随环境变化"的正向判据。故"看到真模糊"这一条在本机 **未获判别性证据**，
            // 需要换一台启用壁纸/可注销生效的 Win11 复测（壁纸法：同一锚点两次截屏，只换壁纸亮度）。
            path = BackdropPath.SystemBackdrop;
        }
        else if (kind != BackdropKind.None)
        {
            // 22H2 之前的未公开 Mica 开关（BOOL=true）；只有 Mica 语义，Acrylic 请求在此档退化为 Mica
            var on = 1;
            micaHr = DwmSetWindowAttribute(hwnd, DwmwaMicaEffect, ref on, sizeof(int));
            if (micaHr == 0)
            {
                path = BackdropPath.MicaLegacy;
            }
            else
            {
                path = ApplyAccentPolicy(hwnd, tint, AccentState.AcrylicBlurBehind)
                    ? BackdropPath.CompositionAccent // Win10 旧亚克力兜底
                    : BackdropPath.Failed;
                Logger.Warn($"旧 accent 兜底 hr(38)=0x{backdropHr:X} hr(1029)=0x{micaHr:X}");
            }
        }
        else
        {
            // 显式关闭：清掉背板 + 关掉旧 accent（否则 Win10 路径残留的 acrylic 不会随主题失效）
            ApplyAccentPolicy(hwnd, Colors.Transparent, AccentState.Disabled);
            path = BackdropPath.None;
        }

        Logger.Info($"背板路径={path} kind={kind} hwnd=0x{hwnd.ToInt64():X} " +
                    $"frame_hr={Hr(frameHr)} backdrop_hr={Hr(backdropHr)} corner_hr={Hr(cornerHr)} " +
                    $"dark_hr={Hr(darkHr)} mica_hr={Hr(micaHr)}");
        return path;
    }

    /// <summary>HRESULT 日志格式：未尝试的属性打 n/a（不把哨兵 -1 打成 0xFFFFFFFF 冒充失败码）。</summary>
    private static string Hr(int? hr) => hr is null ? "n/a" : $"0x{hr.Value:X}";

    /// <summary>旧 SetWindowCompositionAttribute 亚克力（ModernFlyouts/MIT 模式）。成功返回 true。</summary>
    internal static bool ApplyAccentPolicy(IntPtr hwnd, Color tint, AccentState state)
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
            Logger.Warn($"旧亚克力降级失败: {ex.Message}");
            return false;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }
}

/// <summary>
/// 亚克力/毛玻璃窗口基类。实现模式参考 ModernFlyouts (MIT) 的 SetWindowCompositionAttribute 用法；
/// Win11 系统背板见 <see cref="SystemBackdrop"/>。
/// <para>
/// 默认仍是"分层窗口"形态（<see cref="Window.AllowsTransparency"/>=true）：DockWindow 依赖它做
/// <c>Background={x:Null}</c> 的两侧点击穿透。要走真背板的窗口须在构造期显式置
/// <c>AllowsTransparency=false</c> 后调用 <see cref="ApplyBackdrop"/>。
/// </para>
/// </summary>
public class AcrylicWindow : Window
{
    public AcrylicWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
    }

    /// <summary>兼容包装：旧 <c>SetWindowCompositionAttribute</c> 亚克力（分层窗口专用，Dock 仍用此路）。</summary>
    internal void ApplyAcrylic(Color tint, AccentState state = AccentState.AcrylicBlurBehind)
        => SystemBackdrop.ApplyAccentPolicy(new WindowInteropHelper(this).EnsureHandle(), tint, state);

    /// <summary>兼容包装：关闭旧亚克力。</summary>
    public void DisableAcrylic() => ApplyAcrylic(Colors.Transparent, AccentState.Disabled);

    /// <summary>
    /// Win11 系统背板（38 → 1029 → 旧亚克力三级降级）。要求窗口 <c>AllowsTransparency=false</c>，
    /// 否则内部自动退化为旧亚克力并记日志（见 <see cref="SystemBackdrop"/> 的互斥说明）。
    /// <para>
    /// <paramref name="tint"/>（主题 AcrylicTint）的作用范围见
    /// <see cref="SystemBackdrop.Apply"/>：档 1/2 的模糊由系统自行取色（壁纸 + 系统强调色），
    /// 我们的 tint 写不进去，只能由内容侧在卡底之上叠一层 <c>ThemeBackdropTint</c> 画刷体现主题；
    /// tint 只有档 3（旧亚克力 GradientColor）与"三档全败"的不透明兜底直接消费。
    /// </para>
    /// </summary>
    public BackdropPath ApplyBackdrop(Color tint, BackdropKind kind = BackdropKind.Acrylic,
                                      bool roundedCorners = true, bool darkTint = true)
        => SystemBackdrop.Apply(this, tint, kind, roundedCorners, darkTint);
}
