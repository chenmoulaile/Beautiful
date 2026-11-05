// Probe: which WinEvents fire for window OPEN / CLOSE, and what state is observable?
// Run with pwsh 7 (Roslyn / C# 12), NOT Windows PowerShell 5.1 -- its CodeDom compiler
// rejects `out var` and modern syntax, which wasted two earlier attempts.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

internal static class WinEventProbe
{
    private delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventDelegate proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    public struct RECT { public int Left, Top, Right, Bottom; }

    private const uint EvCreate = 0x8000, EvDestroy = 0x8001, EvShow = 0x8002, EvHide = 0x8003;
    private const uint EvCloaked = 0x8017, EvUncloaked = 0x8018;
    private const uint EvMinStart = 0x0016, EvMinEnd = 0x0017;
    private const uint OutOfContext = 0x0000, SkipOwn = 0x0002;
    private const int DwmwaCloaked = 14;

    private static readonly List<string> Log = [];
    // System.Threading.Lock is .NET 9+; this tool targets net8.0-windows, so use a plain object gate.
    private static readonly object Gate = new();
    private static WinEventDelegate? _proc;
    private static int _watchPid;

    public static void Start(string procName)
    {
        _proc = OnEvent;
        foreach (var e in new[] { EvCreate, EvDestroy, EvShow, EvHide, EvCloaked, EvUncloaked, EvMinStart, EvMinEnd })
            SetWinEventHook(e, e, IntPtr.Zero, _proc, 0, 0, OutOfContext | SkipOwn);

        var t = new Thread(() =>
        {
            for (var i = 0; i < 150; i++)
            {
                var found = Process.GetProcessesByName(procName);
                if (found.Length > 0)
                {
                    _watchPid = found[0].Id;
                    lock (Gate) Log.Add($"--- armed on pid {_watchPid} ---");
                    return;
                }
                Thread.Sleep(100);
            }
            lock (Gate) Log.Add($"--- NEVER SAW {procName} ---");
        });
        t.IsBackground = true;
        t.Start();
    }

    private static string Name(uint e) => e switch
    {
        EvCreate => "OBJECT_CREATE",
        EvDestroy => "OBJECT_DESTROY",
        EvShow => "OBJECT_SHOW",
        EvHide => "OBJECT_HIDE",
        EvCloaked => "OBJECT_CLOAKED",
        EvUncloaked => "OBJECT_UNCLOAKED",
        EvMinStart => "MINIMIZESTART",
        EvMinEnd => "MINIMIZEEND",
        _ => "0x" + e.ToString("X4"),
    };

    private static void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;
            var watch = _watchPid;
            if (watch == 0) return;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != (uint)watch) return;

            var cls = new StringBuilder(256);
            GetClassName(hwnd, cls, 256);
            var alive = IsWindow(hwnd);
            var rect = "n/a";
            var cloaked = -1;
            if (alive && GetWindowRect(hwnd, out var r)) rect = $"{r.Left},{r.Top},{r.Right},{r.Bottom}";
            if (alive && DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var c, sizeof(int)) == 0) cloaked = c;
            var line = $"[{time,7}ms] {Name(evt),-16} hwnd=0x{hwnd.ToInt64():X} alive={alive,-5} " +
                       $"iconic={(alive ? IsIconic(hwnd).ToString() : "-"),-5} vis={(alive ? IsWindowVisible(hwnd).ToString() : "-"),-5} " +
                       $"cloaked={cloaked,-3} rect={rect} cls={cls}";
            lock (Gate) Log.Add(line);
        }
        catch { }
    }

    public static string[] Dump() { lock (Gate) return [.. Log]; }
}

internal static class Probe
{
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint f, uint t, out IntPtr r);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out WinEventProbe.RECT r);

    public static void Main(string[] args)
    {
        var target = args.Length > 0 ? args[0] : "notepad";
        Console.WriteLine($"install hooks; watching for '{target}'");
        WinEventProbe.Start(target);

        Thread.Sleep(400);
        Console.WriteLine($"launching {target}");
        var proc = Process.Start(target);
        if (proc is null) { Console.WriteLine("launch failed"); return; }
        Thread.Sleep(4000);

        proc.Refresh();
        var main = proc.MainWindowHandle;
        Console.WriteLine($"main hwnd = 0x{main.ToInt64():X}");

        if (main != IntPtr.Zero)
        {
            Console.WriteLine("ACTION minimize (SW_MINIMIZE=6)");
            ShowWindow(main, 6);
            Thread.Sleep(1800);

            Console.WriteLine("ACTION restore (SW_RESTORE=9)");
            ShowWindow(main, 9);
            Thread.Sleep(1800);

            Console.WriteLine("ACTION close (WM_CLOSE)");
            SendMessageTimeout(main, 0x0010, IntPtr.Zero, IntPtr.Zero, 2, 2000, out _);
            Thread.Sleep(2500);
        }

        Console.WriteLine();
        Console.WriteLine("=== EVENT LOG ===");
        var lines = WinEventProbe.Dump();
        File.WriteAllLines(@"D:\Beautiful\tools\winevent-probe.txt", lines, Encoding.UTF8);
        foreach (var l in lines) Console.WriteLine(l);
        Console.WriteLine();
        Console.WriteLine($"total {lines.Length} lines -> tools\\winevent-probe.txt");
    }
}
