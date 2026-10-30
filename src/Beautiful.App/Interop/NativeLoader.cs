using System;
using System.IO;
using System.Runtime.InteropServices;
using Beautiful.App.Services;
using Beautiful.Core.Native;

namespace Beautiful.App.Interop;

/// <summary>
/// Native 层唯一加载/降级入口（v4 Phase 2 T1）。
/// <para>
/// 纯 C# 形态是产品底线：DLL 缺失、坏镜像、`BEAUTIFUL_NONATIVE=1`、任何 P/Invoke 异常，
/// 都只把 <see cref="Available"/> 置 false 并留中文日志，绝不把异常抛给 UI/启动序。
/// 所有调用先过这里的 Guard；native 模块失效不允许拖垮主程序。
/// </para>
/// </summary>
public static class NativeLoader
{
    private static readonly object Gate = new();
    private static bool _probed;
    private static bool _available;
    private static string _status = "未探测";
    private static NativeHealth _health;

    public static bool Available { get { EnsureProbed(); return _available; } }
    public static string Status { get { EnsureProbed(); return _status; } }
    public static NativeHealth Health { get { EnsureProbed(); return _health; } }
    public static string Version => Available ? _health.Version : "未加载";
    public static uint Modules => Available ? _health.Modules : 0;


    private static void EnsureProbed()
    {
        if (_probed) return;
        lock (Gate)
        {
            if (_probed) return;
            _probed = true;
            Probe();
        }
    }

    private static void Probe()
    {
        if (Environment.GetEnvironmentVariable("BEAUTIFUL_NONATIVE") == "1")
        {
            _available = false;
            _status = "已由 BEAUTIFUL_NONATIVE=1 强制降级";
            Logger.Info($"Native 层：{_status}");
            return;
        }

        var dllPath = Path.Combine(AppContext.BaseDirectory, NativeApi.Dll);
        if (!File.Exists(dllPath))
        {
            _available = false;
            _status = "Beautiful.Native.dll 缺失（纯 C# 形态）";
            Logger.Warn($"Native 层：{_status}");
            return;
        }

        try
        {
            var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            Environment.SetEnvironmentVariable("BEAUTIFUL_NATIVE_LOG_DIR", logDir);

            var abi = NativeApi.Bn_AbiVersion();
            if (abi != NativeProtocol.AbiVersion)
            {
                _available = false;
                _status = $"ABI 不匹配（native={abi}, app={NativeProtocol.AbiVersion}）";
                Logger.Warn($"Native 层：{_status}");
                return;
            }
            var status = NativeApi.Bn_GetHealth(out _health);
            if (status != 0)
            {
                _available = false;
                _status = $"健康检查失败（code={status}）";
                Logger.Warn($"Native 层：{_status}");
                return;
            }
            _available = true;
            _status = $"已加载 v{_health.Version} modules=0x{_health.Modules:X}";
            Logger.Info($"Native 层：{_status}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            _available = false;
            _status = $"加载失败：{ex.GetType().Name}";
            Logger.Warn($"Native 层：{_status} {ex.Message}");
        }
        catch (Exception ex)
        {
            _available = false;
            _status = $"探测异常：{ex.GetType().Name}";
            Logger.Warn($"Native 层：{_status} {ex.Message}");
        }
    }

    private static int Guard(Func<int> action, int fallback = -2)
    {
        if (!Available) return fallback;
        try { return action(); }
        catch (Exception ex)
        {
            Logger.Warn($"Native 调用失败：{ex.GetType().Name} {ex.Message}");
            return -99;
        }
    }

    private static T Guard<T>(Func<T> action, T fallback)
    {
        if (!Available) return fallback;
        try { return action(); }
        catch (Exception ex)
        {
            Logger.Warn($"Native 调用失败：{ex.GetType().Name} {ex.Message}");
            return fallback;
        }
    }

    public static int MaterialProbe() => Guard(NativeApi.Bn_Material_Probe);

    public static IntPtr MaterialCreate(IntPtr owner, ref NativeMaterialDesc desc)
    {
        if (!Available) return IntPtr.Zero;
        try { return NativeApi.Bn_Material_Create(owner, ref desc); }
        catch (Exception ex) { Logger.Warn($"MaterialCreate: {ex.Message}"); return IntPtr.Zero; }
    }

    public static int MaterialSetRect(IntPtr handle, ref NativeRect rect)
    {
        if (!Available) return NativeProtocol.NotAvailable;
        try { return NativeApi.Bn_Material_SetRect(handle, ref rect); }
        catch (Exception ex) { Logger.Warn($"MaterialSetRect: {ex.Message}"); return NativeProtocol.Unknown; }
    }

    public static int MaterialSetTheme(IntPtr handle, uint tint, float opacity, int dark)
        => Guard(() => NativeApi.Bn_Material_SetTheme(handle, tint, opacity, dark));
    public static int MaterialShow(IntPtr handle, bool show)
        => Guard(() => NativeApi.Bn_Material_Show(handle, show ? 1 : 0));
    public static void MaterialDestroy(IntPtr handle)
    {
        if (!Available || handle == IntPtr.Zero) return;
        try { NativeApi.Bn_Material_Destroy(handle); } catch (Exception ex) { Logger.Warn($"MaterialDestroy: {ex.Message}"); }
    }

    public static IntPtr CaptureCreate() => Guard(NativeApi.Bn_Capture_Create, IntPtr.Zero);

    public static int CaptureWindow(IntPtr handle, IntPtr hwnd, byte[] pixels, out NativeCaptureResult result)
    {
        result = default;
        if (!Available) return NativeProtocol.NotAvailable;
        try { return NativeApi.Bn_Capture_Window(handle, hwnd, pixels, pixels.Length, out result); }
        catch (Exception ex) { Logger.Warn($"CaptureWindow: {ex.Message}"); return NativeProtocol.Unknown; }
    }

    public static int CaptureScreen(IntPtr handle, ref NativeRect rect, byte[] pixels, out NativeCaptureResult result)
    {
        result = default;
        if (!Available) return NativeProtocol.NotAvailable;
        try { return NativeApi.Bn_Capture_Screen(handle, ref rect, pixels, pixels.Length, out result); }
        catch (Exception ex) { Logger.Warn($"CaptureScreen: {ex.Message}"); return NativeProtocol.Unknown; }
    }

    public static void CaptureDestroy(IntPtr handle)
    {
        if (!Available || handle == IntPtr.Zero) return;
        try { NativeApi.Bn_Capture_Destroy(handle); } catch (Exception ex) { Logger.Warn($"CaptureDestroy: {ex.Message}"); }
    }

    public static int SceneProbe() => Guard(NativeApi.Bn_Scene_Probe);
    public static IntPtr SceneCreate(IntPtr owner, int width, int height)
        => Guard(() => NativeApi.Bn_Scene_Create(owner, width, height), IntPtr.Zero);

    public static int SceneRenderTest(IntPtr handle, byte[] pixels, out NativeCaptureResult result)
    {
        result = default;
        if (!Available) return NativeProtocol.NotAvailable;
        try { return NativeApi.Bn_Scene_RenderTest(handle, pixels, pixels.Length, out result); }
        catch (Exception ex) { Logger.Warn($"SceneRenderTest: {ex.Message}"); return NativeProtocol.Unknown; }
    }

    public static void SceneDestroy(IntPtr handle)
    {
        if (!Available || handle == IntPtr.Zero) return;
        try { NativeApi.Bn_Scene_Destroy(handle); } catch (Exception ex) { Logger.Warn($"SceneDestroy: {ex.Message}"); }
    }

    public static int InjectProbe() => Guard(NativeApi.Bn_Inject_Probe);
    public static int InjectExplorer(string dllPath)
        => Guard(() => NativeApi.Bn_Inject_Explorer(dllPath));
    public static int InjectUnload() => Guard(NativeApi.Bn_Inject_Unload);

    public static string LastError()
    {
        if (!Available) return _status;
        try
        {
            var ptr = NativeApi.Bn_LastError();
            return ptr == IntPtr.Zero ? "" : Marshal.PtrToStringUni(ptr) ?? "";
        }
        catch { return ""; }
    }
}
