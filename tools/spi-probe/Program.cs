using System.Runtime.InteropServices;

var info = new AnimInfo { Size = (uint)Marshal.SizeOf<AnimInfo>(), MinAnimate = 42 };  // 填哨兵值：GET 失败时能分辨“没写”和“写成了0”
var ok = Native.SystemParametersInfo(0x0042, info.Size, ref info, 0);
Console.WriteLine($"GET ok={ok} minAnimate={info.MinAnimate} err={Marshal.GetLastWin32Error()}");

if (!ok)
{
    // GET 失败：直接试 SET(0)，再 GET 回来看有没有生效（有些系统 GET 被限制但 SET 仍可用）
    var setOnly = new AnimInfo { Size = info.Size, MinAnimate = 0 };
    var okSet = Native.SystemParametersInfo(0x0043, setOnly.Size, ref setOnly, 0);
    var verify = new AnimInfo { Size = info.Size, MinAnimate = 42 };
    var okVerify = Native.SystemParametersInfo(0x0042, verify.Size, ref verify, 0);
    Console.WriteLine($"盲试 SET(0) ok={okSet}，回读 ok={okVerify} 值={verify.MinAnimate}");
}

if (ok && info.MinAnimate != 0)
{
    var set = new AnimInfo { Size = info.Size, MinAnimate = 0 };
    var okSet = Native.SystemParametersInfo(0x0043, set.Size, ref set, 0);
    Console.WriteLine($"SET(0) ok={okSet} err={Marshal.GetLastWin32Error()}");
    var back = new AnimInfo { Size = info.Size, MinAnimate = -1 };
    var okBack = Native.SystemParametersInfo(0x0042, back.Size, ref back, 0);
    Console.WriteLine($"GET2 ok={okBack} minAnimate={back.MinAnimate}");
    var restore = new AnimInfo { Size = back.Size, MinAnimate = info.MinAnimate };
    var okRes = Native.SystemParametersInfo(0x0043, restore.Size, ref restore, 0);
    Console.WriteLine($"RESTORE({info.MinAnimate}) ok={okRes}");
}

[StructLayout(LayoutKind.Sequential)]
internal struct AnimInfo
{
    public uint Size;
    public int MinAnimate;
}

internal static class Native
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint action, uint param, ref AnimInfo info, uint winIni);
}
