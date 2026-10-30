using System.Windows.Interop;
using Beautiful.App.Interop;

namespace Beautiful.App.Services;

/// <summary>
/// 系统"应用模式"（亮/暗）探测与变更通知（v4 Phase 1 T4，D-VERSION 的运行时半边）。
/// <para>
/// 读 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme</c>
/// （DWORD，0=深色，1=浅色；缺失按深色）。变更由 <c>WM_SETTINGCHANGE</c> 驱动：
/// 注册一个 HWND_MESSAGE 消息窗，收到广播后重读注册表，值变了才触发 <see cref="Changed"/>，
/// 避免一次主题切换的多条广播引起重复换字典。空闲零轮询。
/// </para>
/// </summary>
public static class SystemAppearance
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static readonly IntPtr HwndMessage = new(-3); // HWND_MESSAGE：只收消息、不可见

    private static HwndSource? _source;
    private static bool _isLight;

    /// <summary>系统应用模式当前是否为浅色（默认深色：Big Sur 暗色形态）。</summary>
    public static bool IsLight => _isLight;

    /// <summary>系统亮暗变更（已重读到新值）。事件在 UI 线程触发。</summary>
    public static event Action? Changed;

    /// <summary>启动监听（幂等）。必须在 WPF UI 线程调用（创建 HwndSource）。</summary>
    public static void Start()
    {
        if (_source is not null) return;
        _isLight = ReadIsLight();
        try
        {
            var parameters = new HwndSourceParameters("Beautiful.SystemAppearance")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ExtendedWindowStyle = 0,
                ParentWindow = HwndMessage,
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
            Logger.Info($"系统外观监听已启动：{(IsLight ? "浅色" : "深色")}");
        }
        catch (Exception ex)
        {
            // 监听失败只影响"跟随系统"的实时性：启动时已读到的值仍然有效
            Logger.Warn($"系统外观监听未启动（跟随系统将只在启动时取样）: {ex.Message}");
        }
    }

    /// <summary>停止监听（退出链路，幂等）。</summary>
    public static void Stop()
    {
        if (_source is null) return;
        try
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warn($"系统外观监听停止失败: {ex.Message}");
        }
        finally
        {
            _source = null;
            Changed = null;
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmSettingChange)
        {
            var now = ReadIsLight();
            if (now != _isLight)
            {
                _isLight = now;
                Logger.Info($"系统外观变更：{(now ? "浅色" : "深色")}");
                Changed?.Invoke();
            }
        }
        return IntPtr.Zero;
    }

    private static bool ReadIsLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception ex)
        {
            Logger.Warn($"读取 AppsUseLightTheme 失败（按深色）: {ex.Message}");
            return false;
        }
    }
}
