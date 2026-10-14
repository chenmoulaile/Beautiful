using System.Runtime.InteropServices;
using System.Windows.Forms;
using Beautiful.App.Controls;
using Beautiful.App.Services;
using Beautiful.App.Themes;
using Point = System.Windows.Point;

namespace Beautiful.App.Shell;

/// <summary>
/// 托盘图标（System.Windows.Forms.NotifyIcon，运行于 WPF UI 线程——Dispatcher 泵送 Win32 消息）。
/// 图标为代码绘制的 16x16 圆点（主题强调色，构建时取值不随主题热更新——托盘图标为静态资源语义）。
/// 右键弹自绘 <see cref="FluentMenu"/>（macOS 质感高级弹层，替代 v1 WinForms ContextMenuStrip 灰皮）：
/// 设置… / 主题▸（鸿蒙/macOS/Linux，勾选=当前主题，每次打开重建即刷新） / 重载 Dock / 分隔 / 退出；
/// 左键双击 = 设置。回调注入（openSettings/reloadDock/exit/applyTheme/currentTheme）——本类不持有
/// Dock/Settings 引用，重载 Dock 等生命周期编排归 App。<see cref="Dispose"/> 必须在退出路径调用
/// （关未决弹层 + 移除图标，防幽灵图标）。
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notify;
    private readonly Icon _icon;
    private readonly bool _ownsIcon; // SystemIcons.* 为进程级共享图标，不可 Dispose；仅自绘图标拥有
    private readonly Action _openSettings;
    private readonly Action _reloadDock;
    private readonly Action _exit;
    private readonly Action<string> _applyTheme;
    private readonly Func<string> _currentTheme;
    private bool _disposed;

    public TrayIconManager(
        Action openSettings,
        Action reloadDock,
        Action exit,
        Action<string> applyTheme,
        Func<string> currentTheme)
    {
        _openSettings = openSettings;
        _reloadDock = reloadDock;
        _exit = exit;
        _applyTheme = applyTheme;
        _currentTheme = currentTheme;

        (_icon, _ownsIcon) = CreateIcon(ThemeManager.Current.AccentHex);
        _notify = new NotifyIcon
        {
            Icon = _icon,
            Text = "Beautiful",
            Visible = true
        };
        _notify.DoubleClick += (_, _) => _openSettings();
        _notify.MouseUp += OnMouseUp;
    }

    /// <summary>
    /// 右键（按钮释放）在光标处弹 FluentMenu。Cursor.Position 为物理像素（进程 PerMonitorV2），
    /// 弹层内部全程物理像素定位（含工作区边缘左右/上下翻转），无 DPI 往返换算误差。
    /// </summary>
    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var p = Cursor.Position;
        FluentMenu.Show(new FluentPopupHost(), new Point(p.X, p.Y), BuildMenuItems(),
            item => item.Invoke?.Invoke());
    }

    /// <summary>菜单树每次打开前重建：勾选态 = 当前主题快照（等价 v1 ContextMenuStrip.Opening 刷新语义）。</summary>
    private List<MenuItemView> BuildMenuItems()
    {
        var theme = _currentTheme();
        return new List<MenuItemView>
        {
            new() { Id = "settings", Label = "设置…", Glyph = "\uE713", Invoke = _openSettings },
            new() { Id = "theme", Label = "主题", Glyph = "\uE790", Children = new List<MenuItemView>
            {
                ThemeItem("harmonyos", "鸿蒙", theme),
                ThemeItem("macos", "macOS", theme),
                ThemeItem("linux", "Linux", theme),
            } },
            new() { Id = "reload", Label = "重载 Dock", Glyph = "\uE72C", Invoke = _reloadDock },
            new() { Id = "sep", IsSeparator = true },
            new() { Id = "quit", Label = "退出", Glyph = "\uE7E8", Invoke = _exit },
        };
    }

    private MenuItemView ThemeItem(string id, string label, string current) =>
        new() { Id = id, Label = label, IsChecked = current == id, Invoke = () => _applyTheme(id) };

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
        FluentMenu.CloseCurrent(); // 退出清理时关闭在显弹层（防孤儿窗口/回调重入）
        _notify.Visible = false;   // 先隐藏再 Dispose：立即从托盘区移除
        _notify.Dispose();
        if (_ownsIcon) _icon.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
