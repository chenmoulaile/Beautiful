using System.Runtime.InteropServices;

namespace Beautiful.Core.Native;

/// <summary>
/// 进程内 C ABI 的固定布局结构体（v4 Phase 2 T2）：C#/C++ 双侧必须逐字节一致。
/// 尺寸由 <c>NativeProtocolTests</c> 与 `tools/NativeSmoke` 双侧断言；改字段顺序/宽度前先改测试。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
public struct NativeHealth
{
    public uint AbiVersion;
    public uint Modules;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Version;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string LastError;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NativeMaterialDesc
{
    public NativeRect Rect;
    public uint Tint;
    public float Opacity;
    public float CornerRadius;
    public int Dark;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NativeCaptureResult
{
    public int Width;
    public int Height;
    public int Stride;
    public int Rejected;
    public int RequiredBytes;
    public int Mode;
}

/// <summary>ABI 常量与布局断言（纯函数，Core 单测直接引用）。</summary>
public static class NativeProtocol
{
    public const int AbiVersion = 1;

    public const uint ModuleMaterial = 1u << 0;
    public const uint ModuleCapture = 1u << 1;
    public const uint ModuleScene = 1u << 2;
    public const uint ModuleInject = 1u << 3;

    public const int Ok = 0;
    public const int InvalidArg = -1;
    public const int NotAvailable = -2;
    public const int NotImplemented = -3;
    public const int OsUnsupported = -4;
    public const int D3D = -5;
    public const int Capture = -6;
    public const int Io = -7;
    public const int Inject = -8;
    public const int Unknown = -99;

    public const int RectSize = 16;
    public const int HealthSize = 584;
    public const int MaterialDescSize = 32;
    public const int CaptureResultSize = 24;

    /// <summary>返回布局偏差说明（空列表 = 双侧一致）。App 启动/单测可调用。</summary>
    public static IReadOnlyList<string> ValidateLayout()
    {
        var errors = new List<string>();
        void Check<T>(int expected) where T : struct
        {
            var actual = Marshal.SizeOf<T>();
            if (actual != expected) errors.Add($"{typeof(T).Name}: {actual} != {expected}");
        }
        Check<NativeRect>(RectSize);
        Check<NativeHealth>(HealthSize);
        Check<NativeMaterialDesc>(MaterialDescSize);
        Check<NativeCaptureResult>(CaptureResultSize);
        return errors;
    }
}
