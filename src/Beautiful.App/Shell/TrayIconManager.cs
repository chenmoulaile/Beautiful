using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Beautiful.App.Services;
using Beautiful.App.Themes;

namespace Beautiful.App.Shell;

/// <summary>
/// 托盘图标（System.Windows.Forms.NotifyIcon，运行于 WPF UI 线程——Dispatcher 泵送 Win32 消息）。
/// 图标为代码绘制的 16x16 圆点（主题强调色，构建时取值不随主题热更新——托盘图标为静态资源语义）。
/// 菜单：设置 / 主题(鸿蒙/macOS/Linux 子菜单，选中态打开时刷新) / 重载 Dock / 退出；双击 = 设置。
/// 回调注入（openSettings/reloadDock/exit/applyTheme/currentTheme）——本类不持有 Dock/Settings 引用，
/// 重载 Dock 等生命周期编排归 App。<see cref="Dispose"/> 必须在退出路径调用（移除图标，防幽灵图标）。
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notify;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _themeHarmony = new("鸿蒙") { CheckOnClick = true };
    private readonly ToolStripMenuItem _themeMacOs = new("macOS") { CheckOnClick = true };
    private readonly ToolStripMenuItem _themeLinux = new("Linux") { CheckOnClick = true };
    private readonly Func<string> _currentTheme;
    private readonly Icon _icon;
    private readonly bool _ownsIcon; // SystemIcons.* 为进程级共享图标，不可 Dispose；仅自绘图标拥有
    private bool _disposed;

    public TrayIconManager(
        Action openSettings,
        Action reloadDock,
        Action exit,
        Action<string> applyTheme,
        Func<string> currentTheme)
    {
        _currentTheme = currentTheme;

        (_icon, _ownsIcon) = CreateIcon(ThemeManager.Current.AccentHex);
        _notify = new NotifyIcon
        {
            Icon = _icon,
            Text = "Beautiful",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _notify.DoubleClick += (_, _) => openSettings();

        _themeHarmony.Click += (_, _) => applyTheme("harmonyos");
        _themeMacOs.Click += (_, _) => applyTheme("macos");
        _themeLinux.Click += (_, _) => applyTheme("linux");
        _menu.Opening += (_, _) => RefreshThemeChecks();

        _menu.Items.Add(new ToolStripMenuItem("设置", null, (_, _) => openSettings()));
        _menu.Items.Add(new ToolStripMenuItem("主题", null, _themeHarmony, _themeMacOs, _themeLinux));
        _menu.Items.Add(new ToolStripMenuItem("重载 Dock", null, (_, _) => reloadDock()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => exit()));
    }

    /// <summary>主题子菜单选中态随当前设置刷新（菜单每次打开时）。</summary>
    private void RefreshThemeChecks()
    {
        var theme = _currentTheme();
        _themeHarmony.Checked = theme == "harmonyos";
        _themeMacOs.Checked = theme == "macos";
        _themeLinux.Checked = theme == "linux";
    }

    /// <summary>代码生成 16x16 圆点图标：主题强调色实心圆 + 抗锯齿。失败兜底系统应用图标（不拥有，勿 Dispose）。</summary>
    private static (Icon icon, bool owned) CreateIcon(string accentHex)
    {
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            using var bmp = new System.Drawing.Bitmap(16, 16);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new System.Drawing.SolidBrush(AccentColor(accentHex));
            g.Clear(System.Drawing.Color.Transparent);
            g.FillEllipse(brush, 1, 1, 14, 14);
            hIcon = bmp.GetHicon();
            // Clone 持有独立数据副本（Dispose 不依赖原 HICON）；原句柄即刻销毁防泄漏
            return ((Icon)Icon.FromHandle(hIcon).Clone(), true);
        }
        catch (Exception ex)
        {
            Logger.Warn($"托盘图标生成失败，回退系统图标: {ex.Message}");
            return (SystemIcons.Application, false);
        }
        finally
        {
            if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
        }
    }

    private static System.Drawing.Color AccentColor(string hex)
    {
        try
        {
            return System.Drawing.ColorTranslator.FromHtml(hex);
        }
        catch
        {
            return System.Drawing.Color.FromArgb(0x0A, 0x84, 0xFF); // macOS 系统蓝兜底
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notify.Visible = false; // 先隐藏再 Dispose：立即从托盘区移除
        _notify.Dispose();
        _menu.Dispose();
        if (_ownsIcon) _icon.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
