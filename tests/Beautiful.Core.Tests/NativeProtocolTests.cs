using System.Runtime.InteropServices;
using Beautiful.Core.Native;
using Xunit;

/// <summary>
/// v4 Phase 2 T2：C ABI 固定布局取证。C# 与 C++ 双侧尺寸必须逐字节一致；
/// C++ 侧由 tools/NativeSmoke 打印同一组 sizeof，偏移错误在联调前就能被拦下。
/// </summary>
public class NativeProtocolTests
{
    [Fact]
    public void 结构体尺寸与Cpp侧一致()
    {
        Assert.Equal(NativeProtocol.RectSize, Marshal.SizeOf<NativeRect>());
        Assert.Equal(NativeProtocol.HealthSize, Marshal.SizeOf<NativeHealth>());
        Assert.Equal(NativeProtocol.MaterialDescSize, Marshal.SizeOf<NativeMaterialDesc>());
        Assert.Equal(NativeProtocol.CaptureResultSize, Marshal.SizeOf<NativeCaptureResult>());
        Assert.Empty(NativeProtocol.ValidateLayout());
    }

    [Fact]
    public void Rect字段顺序与偏移()
    {
        var rect = new NativeRect { Left = 1, Top = 2, Right = 3, Bottom = 4 };
        Assert.Equal(1, rect.Left);
        Assert.Equal(2, rect.Top);
        Assert.Equal(3, rect.Right);
        Assert.Equal(4, rect.Bottom);
        Assert.Equal(0, Marshal.OffsetOf<NativeRect>(nameof(NativeRect.Left)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<NativeRect>(nameof(NativeRect.Top)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<NativeRect>(nameof(NativeRect.Right)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<NativeRect>(nameof(NativeRect.Bottom)).ToInt32());
    }

    [Fact]
    public void ABI常量与错误码固定()
    {
        Assert.Equal(1, NativeProtocol.AbiVersion);
        Assert.Equal(0, NativeProtocol.Ok);
        Assert.Equal(-1, NativeProtocol.InvalidArg);
        Assert.Equal(-2, NativeProtocol.NotAvailable);
        Assert.Equal(-99, NativeProtocol.Unknown);
        Assert.Equal(1u, NativeProtocol.ModuleMaterial);
        Assert.Equal(2u, NativeProtocol.ModuleCapture);
        Assert.Equal(4u, NativeProtocol.ModuleScene);
        Assert.Equal(8u, NativeProtocol.ModuleInject);
    }
}
