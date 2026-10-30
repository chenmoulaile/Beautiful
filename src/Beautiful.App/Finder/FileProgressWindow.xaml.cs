using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace Beautiful.App.Finder;

/// <summary>
/// 文件操作进度卡（Phase 6-c）：macOS 拷贝进度窗的形态——无红绿灯、只有"停止"、完成后自动消失。
/// <para>
/// 生命周期由调用方掌握：<see cref="Show"/> → 多次 <see cref="Report"/> → <see cref="Finish"/>。
/// 取消通过 <see cref="Token"/> 传递（Sink 在下一项开始前返回 E_ABORT 中断 Shell 操作），
/// 因此"停止"是**软中断**：当前项会做完，不会留下半个文件——这正是 macOS 的行为。
/// </para>
/// <para>
/// 失败不弹模态框：把标题换成"未能完成"、副标题写原因、按钮变成"关闭"，用户看完自己关；
/// 只有成功/取消才自动关闭，避免"操作成功却要再点一下"的多余动作。
/// </para>
/// </summary>
public partial class FileProgressWindow : Window
{
    private readonly CancellationTokenSource _cts = new();
    private bool _finished;

    private FileProgressWindow(string title, int total)
    {
        InitializeComponent();
        TitleText.Text = title;
        Title = title;
        Bar.Maximum = Math.Max(1, total);
        CountText.Text = $"0 / {total}";
        DetailText.Text = "准备中…";
    }

    /// <summary>取消令牌：交给 <see cref="FileOperationService"/>，由它传给 Sink。</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>打开进度卡（调用方负责在操作结束后调用 <see cref="Finish"/>）。</summary>
    public static FileProgressWindow Show(string title, int total, Window? owner = null)
    {
        var window = new FileProgressWindow(title, total);
        if (owner is { IsLoaded: true })
        {
            window.Owner = owner;
        }

        window.Show();
        return window;
    }

    /// <summary>更新进度（可在任意线程调用，内部封送到 UI）。</summary>
    public void Report(FileOperationProgress progress)
    {
        if (_finished)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Report(progress)));
            return;
        }

        Bar.Value = Math.Min(progress.Completed, Bar.Maximum);
        CountText.Text = $"{progress.Completed} / {progress.Total}";
        if (!string.IsNullOrEmpty(progress.CurrentName))
        {
            DetailText.Text = progress.CurrentName;
        }
    }

    /// <summary>收尾：成功/取消自动关闭；失败保留窗口显示原因。</summary>
    public void Finish(FileOperationResult result, string successVerb)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Finish(result, successVerb)));
            return;
        }

        _finished = true;
        if (result.Success)
        {
            TitleText.Text = $"{successVerb}完成";
            DetailText.Text = $"共 {result.Completed} 项";
            Bar.Value = Bar.Maximum;
            Close();
            return;
        }

        if (result.Cancelled)
        {
            TitleText.Text = "已停止";
            DetailText.Text = $"已完成 {result.Completed} 项";
            Close();
            return;
        }

        TitleText.Text = "未能完成";
        DetailText.Text = result.Error ?? "未知原因";
        DetailText.Foreground = (System.Windows.Media.Brush)FindResource("MacTextPrimary");
        CountText.Text = result.Failed > 0 ? $"失败 {result.Failed} 项" : string.Empty;
        ActionButton.Content = "关闭";
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_finished)
        {
            Close();
            return;
        }

        // 第一次点击 = 停止：禁用按钮避免重复触发，等 Sink 在下一项前中断。
        ActionButton.IsEnabled = false;
        ActionButton.Content = "正在停止…";
        TitleText.Text = "正在停止…";
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 操作已经结束：忽略
        }
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已抬起：无操作
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _finished = true;
        _cts.Dispose();
        base.OnClosed(e);
    }

    /// <summary>WindowStyle=None 后显式请求 DWM 圆角（非分层窗口同样被 DWM 裁剪）。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var corner = NativeMethods.DwmwcpRound;
        NativeMethods.DwmSetWindowAttribute(
            new WindowInteropHelper(this).Handle,
            NativeMethods.DwmwaWindowCornerPreference, ref corner, sizeof(int));
    }
}
