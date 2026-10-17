using System.Dynamic;
using System.Runtime.InteropServices;

namespace Beautiful.App.Services;

/// <summary>
/// 内置屏亮度（控制中心的"亮度"滑杆）。
/// <para>
/// 【为什么走 WMI 而不是 gdi32 的 <c>SetMonitorBrightness</c>】后者是 DDC/CI，只对外接显示器有效，
/// 笔记本内置面板（本机 <c>DISPLAY\EDO0142</c>）不响应。内置屏的唯一正道是 <c>root\WMI</c> 的
/// <c>WmiMonitorBrightness</c>（读）+ <c>WmiMonitorBrightnessMethods.WmiSetBrightness</c>（写）。
/// </para>
/// <para>
/// 【为什么用 <c>WbemScripting.SWbemLocator</c> 而不是手写 <c>IWbemServices</c>】后者要按 vtable
/// 顺序声明 <c>ExecQuery</c> 之前的十几个槽位，错一个就是内存踩踏；而 WMI 的自动化接口
/// （IDispatch）是微软公开且几十年没动过的表面，C# 的 <c>dynamic</c> 直接把它当对象用。
/// 零新增包、零手写 vtable，代价是依赖 <c>wbemdisp</c> 注册（桌面 Windows 一定有）。
/// </para>
/// <para>
/// 本机实测（2026-10-17）：<c>InstancesOf('WmiMonitorBrightness')</c> 读到 61，
/// <c>WmiMonitorBrightnessMethods</c> 上有 <c>WmiSetBrightness</c>。读失败/没有这类（台式机无 WMI 亮度）
/// 一律判不可用并给出原因——不画一根拖了没反应的滑杆。
/// </para>
/// </summary>
public static class BrightnessService
{
    private static dynamic? _services;
    private static bool _tried;
    private static string? _unavailable;

    /// <summary>不可用原因（null = 可用）。直接进界面文案，与音量行的"接口不可用"同一口径。</summary>
    public static string? UnavailableReason => _unavailable;

    /// <summary>当前亮度百分比；不可用返回 -1。</summary>
    public static int Get()
    {
        var svc = Services();
        if (svc is null) return -1;
        try
        {
            foreach (var item in (IEnumerable<object>)svc.InstancesOf("WmiMonitorBrightness"))
            {
                dynamic o = item;
                // 落到 object 上再判：dynamic 实参会让整个调用走运行期绑定器，
                // 而绑定器不接受 `out var`（推不出类型）。
                object? raw = o.Properties_.Item("CurrentBrightness").Value;
                if (raw is not null && TryConvert(raw, out int percent)) return percent;
            }
            _unavailable = "这台机器没有可读的亮度接口（台式机或虚拟显卡常见）";
            return -1;
        }
        catch (Exception ex) when (IsCom(ex))
        {
            _unavailable = $"亮度读取失败：{ex.GetType().Name}";
            return -1;
        }
    }

    /// <summary>设亮度（0-100）。多块内置屏一起设——用户只有一根滑杆，分屏亮度不一致更像 bug。</summary>
    public static bool Set(int percent)
    {
        var svc = Services();
        if (svc is null) return false;
        percent = Math.Clamp(percent, 0, 100);
        try
        {
            var applied = false;
            foreach (var item in (IEnumerable<object>)svc.InstancesOf("WmiMonitorBrightnessMethods"))
            {
                dynamic instance = item;
                dynamic input = instance.Methods_.Item("WmiSetBrightness").InParameters.SpawnInstance_();
                input.Properties_.Item("Brightness").Value = (byte)percent;
                input.Properties_.Item("Timeout").Value = 0u;   // 秒：0 = 立即
                instance.ExecMethod_("WmiSetBrightness", input);
                applied = true;
            }
            if (!applied) _unavailable = "这台机器没有 WmiMonitorBrightnessMethods（驱动未暴露亮度接口）";
            return applied;
        }
        catch (Exception ex) when (IsCom(ex))
        {
            _unavailable = $"亮度设置失败：{ex.GetType().Name}";
            Logger.Warn($"BrightnessService.Set 失败: {ex.Message}");
            return false;
        }
    }

    private static dynamic? Services()
    {
        if (_tried) return _services;
        _tried = true;
        try
        {
            var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator")
                       ?? throw new COMException("WbemScripting.SWbemLocator 未注册");
            dynamic locator = Activator.CreateInstance(type)!;
            _services = locator.ConnectServer(".", @"root\wmi");
            _unavailable = null;
        }
        catch (Exception ex) when (IsCom(ex) || ex is InvalidOperationException)
        {
            _services = null;
            _unavailable = $"WMI 不可用：{ex.GetType().Name}";
        }
        return _services;
    }

    /// <summary>WMI 把 uint8 送回来时可能是 byte/short/int，甚至带符号——统一折算成 0-100 的整数。</summary>
    private static bool TryConvert(object raw, out int percent)
    {
        try
        {
            percent = Convert.ToInt32(raw);
            return percent is >= 0 and <= 100;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            percent = -1;
            return false;
        }
    }

    /// <summary>
    /// 只吞"这台机器的 WMI 不配合"这一类：COM 层异常 + 动态绑定失败。
    /// 后者按类型名判而不写 <c>RuntimeBinderException</c>——那个类型住在 Microsoft.CSharp 里，
    /// 把它写进 using 会让本文件多一条对绑定器程序集的显式依赖，而这里只想知道"是不是它"。
    /// </summary>
    private static bool IsCom(Exception ex) => ex is COMException or InvalidComObjectException
                                               or NotSupportedException
                                               || ex.GetType().Name == "RuntimeBinderException";
}
