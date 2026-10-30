using System.Runtime.InteropServices;
using Beautiful.Core.Native;

namespace Beautiful.App.Interop;

/// <summary>
/// Beautiful.Native 的 C ABI 声明（与 src/Beautiful.Native/beautiful_native.h 逐字段对应）。
/// 结构体定义在 <see cref="NativeProtocol"/>（Core），尺寸由 Core 单测 + NativeSmoke 双侧断言。
/// </summary>
internal static class NativeApi
{
    internal const string Dll = "Beautiful.Native.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint Bn_AbiVersion();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bn_GetHealth(out NativeHealth health);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr Bn_LastError();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bn_Material_Probe();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr Bn_Material_Create(IntPtr owner, ref NativeMaterialDesc desc);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bn_Material_SetRect(IntPtr handle, ref NativeRect rect);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bn_Material_SetTheme(IntPtr handle, uint tint, float opacity, int dark);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bn_Material_Show(IntPtr handle, int show);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Bn_Material_Destroy(IntPtr handle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr Bn_Capture_Create();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bn_Capture_Window(IntPtr handle, IntPtr hwnd, byte[] pixels, int capBytes, out NativeCaptureResult result);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bn_Capture_Screen(IntPtr handle, ref NativeRect rect, byte[] pixels, int capBytes, out NativeCaptureResult result);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Bn_Capture_Destroy(IntPtr handle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bn_Scene_Probe();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr Bn_Scene_Create(IntPtr owner, int width, int height);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bn_Scene_RenderTest(IntPtr handle, byte[] pixels, int capBytes, out NativeCaptureResult result);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Bn_Scene_Destroy(IntPtr handle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bn_Inject_Probe();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int Bn_Inject_Explorer([MarshalAs(UnmanagedType.LPWStr)] string dllPath);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bn_Inject_Unload();
}
