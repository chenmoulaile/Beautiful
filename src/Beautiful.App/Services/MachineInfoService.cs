using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Beautiful.App.Services;

/// <summary>
/// "关于本机"的只读机器信息（v4 Phase 4-T2）：全部来自注册表/环境/内核 API，不装 WMI 包、不求管理员。
/// 取不到就给"—"并留一行日志，绝不编造型号/内存。
/// </summary>
public static class MachineInfoService
{
    public static string MachineName => Environment.MachineName;

    public static string OsVersion
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                var product = key?.GetValue("ProductName") as string ?? "Windows";
                var display = key?.GetValue("DisplayVersion") as string ?? "";
                var build = key?.GetValue("CurrentBuildNumber") as string ?? Environment.OSVersion.Version.Build.ToString();
                // Windows 11 的注册表 ProductName 仍是 "Windows 10 ..."（系统历史遗留）；按 build 纠正。
                if (int.TryParse(build, out var buildNo) && buildNo >= 22000 && product.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
                    product = product.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
                return $"{product} {display}（Build {build}）".Replace("  ", " ").Trim();
            }
            catch (Exception ex)
            {
                Logger.Warn($"读取 Windows 版本失败：{ex.Message}");
                return $"Windows（Build {Environment.OSVersion.Version.Build}）";
            }
        }
    }

    public static string Cpu
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "—";
            }
            catch { return "—"; }
        }
    }

    public static string Model
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                var manufacturer = (key?.GetValue("SystemManufacturer") as string)?.Trim();
                var product = (key?.GetValue("SystemProductName") as string)?.Trim();
                var value = string.Join(' ', new[] { manufacturer, product }.Where(s => !string.IsNullOrWhiteSpace(s)));
                return value.Length > 0 ? value : "—";
            }
            catch { return "—"; }
        }
    }

    public static string Memory
    {
        get
        {
            try
            {
                if (GetPhysicallyInstalledSystemMemory(out var kb) && kb > 0)
                    return $"{kb / 1024.0 / 1024.0:0.#} GB";
            }
            catch { }
            return "—";
        }
    }

    /// <summary>当前桌面壁纸路径；幻灯片/纯色桌面返回 null（调用方用渐变底，不伪造图片）。</summary>
    public static string? WallpaperPath()
    {
        try
        {
            var sb = new StringBuilder(NativeWallpaper.MaxPath);
            if (NativeWallpaper.SystemParametersInfo(NativeWallpaper.SpiGetDeskWallpaper, sb.Capacity, sb, 0))
            {
                var path = sb.ToString();
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
            }
        }
        catch { }
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    private static class NativeWallpaper
    {
        public const int SpiGetDeskWallpaper = 0x0073;
        public const int MaxPath = 260;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool SystemParametersInfo(int action, int param, StringBuilder value, int winIni);
    }
}
