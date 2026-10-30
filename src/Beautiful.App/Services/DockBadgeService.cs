using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Threading;
using Beautiful.App.Dock;
using Beautiful.Core.Dock;

namespace Beautiful.App.Services;

/// <summary>
/// Dock 角标服务（Phase 7-T2）：每 3 秒扫一次运行窗口标题，给白名单应用算未读数并打到 Dock 图标上。
/// <para>
/// 【为什么只做白名单】Windows 没有"应用未读数"这样的系统级概念：微信/QQ 是把 <c>(3)</c> 写在
/// **窗口标题**里的，其它应用（浏览器、邮件）根本没有可读的未读语义。与其对全部窗口瞎猜，
/// 不如只认这几个标题约定明确的应用——没有角标是"不知道"，错角标是"撒谎"。
/// </para>
/// <para>
/// 【多窗口取最大】同一应用开多个窗口时，标题里的数字通常是"该窗口未读"，不是应用总量；
/// 取最大值比求和更接近用户预期（求和会把同一条消息在不同窗口里重复计数）。
/// </para>
/// <para>
/// 【隐私与开销】只读窗口标题，不读内容、不写盘；`ParseCountFromTitle` 是纯函数且有单测。
/// 3 秒一次、只看已跟踪的窗口列表（不额外 EnumWindows），空闲开销可忽略。
/// </para>
/// </summary>
internal sealed class DockBadgeService : IDisposable
{
    /// <summary>能解析出未读数的应用（exe 文件名小写）。扩大范围前请先确认真机标题格式。</summary>
    private static readonly HashSet<string> Whitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "wechat.exe",       // 微信 4.x
        "wechatappex.exe",  // 微信小程序宿主
        "qq.exe",           // QQ
        "tim.exe"           // TIM
    };

    private readonly DockViewModel _vm;
    private readonly DockSurface _surface;
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public DockBadgeService(DockViewModel viewModel, DockSurface surface, Dispatcher dispatcher)
    {
        _vm = viewModel;
        _surface = surface;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background,
            (_, _) => Refresh(), dispatcher);
        _timer.Start();
        Refresh();   // 首帧就把已有窗口算一遍，不用等 3 秒
    }

    /// <summary>立刻重算一次（Dock 重建后由窗口调用，避免角标空 3 秒）。</summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _vm.RunningWindows)
            {
                string exe;
                try
                {
                    // 窗口项的 LaunchPath 就是跟踪器记下的 exe 路径（见 DockViewModel.ToDockItem）
                    exe = Path.GetFileName(item.LaunchPath);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(exe) || !Whitelist.Contains(exe))
                {
                    continue;
                }

                var count = DockBadge.ParseCountFromTitle(item.Label);
                if (count <= 0)
                {
                    continue;
                }

                counts[exe] = counts.TryGetValue(exe, out var existing) ? Math.Max(existing, count) : count;
            }

            var badges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in counts)
            {
                if (DockBadge.Format(pair.Value) is { } text)
                {
                    badges[pair.Key] = text;
                }
            }

            _surface.ApplyBadges(badges);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Dock 角标刷新失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
    }
}
