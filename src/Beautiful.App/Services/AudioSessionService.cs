using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Beautiful.App.Interop;

namespace Beautiful.App.Services;

/// <summary>一个应用在音频混音器里的条目（按进程聚合；同一进程的多条会话共用一组音量/静音）。</summary>
/// <param name="ProcessId">进程号（会话的归属，用于回写）。</param>
/// <param name="Name">显示名（进程名，去掉 .exe；取不到时写"未知应用"）。</param>
/// <param name="ExecutablePath">可执行文件路径（取图标用；取不到为 null）。</param>
/// <param name="VolumePercent">当前音量百分比（0-100）。</param>
/// <param name="Muted">是否静音。</param>
/// <param name="IsSystemSounds">是否为系统提示音会话。</param>
public sealed record AudioSessionInfo(
    uint ProcessId,
    string Name,
    string? ExecutablePath,
    int VolumePercent,
    bool Muted,
    bool IsSystemSounds);

/// <summary>
/// 逐应用混音服务（Phase 7-T5）：枚举默认播放端点上的音频会话，读/写每个应用的音量与静音。
/// <para>
/// 纯 C#，无注入、无系统写入之外的特权：<c>ISimpleAudioVolume</c> 改的是**本会话**的音量，
/// 与系统总音量互不影响——这正是 macOS"逐应用音量"的语义，也是 MyDockFinder 的招牌功能之一。
/// </para>
/// <para>
/// 会话列表是**快照**：面板打开时枚举一次，用户点"刷新"再枚举。不订阅
/// <c>IAudioSessionNotification</c>——那需要维护一个 COM 回调对象并处理进程退出的竞态，
/// 对一个瞬态面板来说是纯负担（同控制中心"每次打开重建"的取舍）。
/// </para>
/// <para>
/// 所有 HRESULT 都自己判：进程退出后 <c>GetProcessId</c>/<c>GetMasterVolume</c> 会返回
/// <c>AUDCLNT_E_DEVICE_INVALIDATED</c> 一类错误，此时跳过该会话，绝不让一条死会话毁掉整张列表。
/// </para>
/// </summary>
public sealed class AudioSessionService : IDisposable
{
    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _device;
    private IAudioSessionManager2? _manager;

    // 会话对象按进程号缓存：写音量时直接复用，避免每次拖动都重新枚举（拖动是高频事件）。
    private readonly List<SessionHandle> _handles = [];

    /// <summary>是否可用（默认端点 + 会话管理器都拿到了）。</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>不可用原因（中文，可直接显示）。</summary>
    public string? UnavailableReason { get; private set; }

    public AudioSessionService()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid(CoreAudioIds.MMDeviceEnumeratorClsid))
                       ?? throw new NotSupportedException("未注册 MMDeviceEnumerator 组件");
            if (Activator.CreateInstance(type) is not IMMDeviceEnumerator enumerator)
            {
                throw new NotSupportedException("MMDeviceEnumerator 接口查询失败");
            }

            _enumerator = enumerator;
            enumerator.GetDefaultAudioEndpoint(CoreAudioIds.DataFlowRender, CoreAudioIds.RoleConsole, out var device);
            _device = device;

            var iid = new Guid(AudioSessionIds.SessionManager2Iid);
            device.Activate(ref iid, CoreAudioIds.CtxServer, IntPtr.Zero, out var raw);
            if (raw is not IAudioSessionManager2 manager)
            {
                throw new NotSupportedException("端点不支持 IAudioSessionManager2");
            }

            _manager = manager;
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            UnavailableReason = $"逐应用混音不可用（{ex.Message}）";
            Logger.Warn($"逐应用混音不可用：{ex.GetType().Name} {ex.Message}");
            Dispose();
        }
    }

    /// <summary>
    /// 枚举当前会话并按进程聚合。返回空列表表示"确实没有应用在出声"（不是失败）。
    /// 失败的会话逐条跳过；整体失败返回空并记日志。
    /// </summary>
    public IReadOnlyList<AudioSessionInfo> Enumerate()
    {
        ReleaseHandles();
        if (!IsAvailable || _manager is null)
        {
            return [];
        }

        var result = new List<AudioSessionInfo>();
        IAudioSessionEnumerator? enumerator = null;
        try
        {
            if (_manager.GetSessionEnumerator(out enumerator) < 0 || enumerator is null)
            {
                return [];
            }

            if (enumerator.GetCount(out var count) < 0)
            {
                return [];
            }

            // 同一进程可能有多条会话（多标签页播放）：先按 pid 分组，音量取第一条。
            var seen = new HashSet<uint>();
            for (var i = 0; i < count; i++)
            {
                if (enumerator.GetSession(i, out var control) < 0 || control is null)
                {
                    continue;
                }

                try
                {
                    var info = Describe(control);
                    if (info is null)
                    {
                        continue;
                    }

                    if (info.Value.SystemSounds)
                    {
                        // 系统提示音不是"应用"，但它是真实存在的一条会话，单独列出更符合 macOS 的呈现
                        if (seen.Add(0))
                        {
                            result.Add(new AudioSessionInfo(0, "系统声音", null,
                                info.Value.VolumePercent, info.Value.Muted, true));
                        }

                        continue;
                    }

                    if (!seen.Add(info.Value.ProcessId))
                    {
                        continue;
                    }

                    result.Add(new AudioSessionInfo(
                        info.Value.ProcessId,
                        ProcessNameOf(info.Value.ProcessId),
                        ExecutableOf(info.Value.ProcessId),
                        info.Value.VolumePercent,
                        info.Value.Muted,
                        false));
                }
                finally
                {
                    Release(control);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            Logger.Warn($"枚举音频会话失败：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            if (enumerator is not null)
            {
                Release(enumerator);
            }
        }

        // 有名字的排前面，系统声音垫底；同名按 pid 稳定排序
        return result
            .OrderBy(info => info.IsSystemSounds ? 1 : 0)
            .ThenBy(info => info.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>设置某个进程（或系统声音，pid=0）的音量百分比；返回是否成功。</summary>
    public bool SetVolume(uint processId, int percent)
    {
        var applied = false;
        foreach (var handle in _handles.Where(h => h.ProcessId == processId))
        {
            var level = Math.Clamp(percent, 0, 100) / 100f;
            var context = Guid.Empty;
            applied |= handle.Volume.SetMasterVolume(level, ref context) >= 0;
        }

        return applied;
    }

    /// <summary>设置静音；返回是否成功。</summary>
    public bool SetMute(uint processId, bool muted)
    {
        var applied = false;
        foreach (var handle in _handles.Where(h => h.ProcessId == processId))
        {
            var context = Guid.Empty;
            applied |= handle.Volume.SetMute(muted, ref context) >= 0;
        }

        return applied;
    }

    public void Dispose()
    {
        ReleaseHandles();
        Release(_manager);
        _manager = null;
        Release(_device);
        _device = null;
        Release(_enumerator);
        _enumerator = null;
        IsAvailable = false;
    }

    // ---------- 内部 ----------

    /// <summary>把一条 <c>IAudioSessionControl</c> 读成 (pid, 音量, 静音, 是否系统声音)。</summary>
    private (uint ProcessId, int VolumePercent, bool Muted, bool SystemSounds)? Describe(IAudioSessionControl control)
    {
        IAudioSessionControl2? control2 = null;
        ISimpleAudioVolume? volume = null;
        try
        {
            control2 = control as IAudioSessionControl2;
            if (control2 is null)
            {
                return null;
            }

            var systemSounds = control2.IsSystemSoundsSession() == 0;   // S_OK 表示"是系统声音"
            uint processId = 0;
            if (!systemSounds && control2.GetProcessId(out processId) < 0)
            {
                return null;   // 进程已退出：跳过这条死会话
            }

            volume = control as ISimpleAudioVolume;
            if (volume is null || volume.GetMasterVolume(out var level) < 0)
            {
                return null;
            }

            volume.GetMute(out var muted);
            _handles.Add(new SessionHandle(processId, volume));
            return (processId, (int)Math.Round(level * 100), muted, systemSounds);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (control2 is not null)
            {
                Release(control2);
            }
        }
    }

    private static string ProcessNameOf(uint pid)
    {
        if (pid == 0)
        {
            return "系统声音";
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            return string.IsNullOrWhiteSpace(name) ? "未知应用" : name;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception)
        {
            return "未知应用";
        }
    }

    private static string? ExecutableOf(uint pid)
    {
        if (pid == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            var path = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private void ReleaseHandles()
    {
        foreach (var handle in _handles)
        {
            Release(handle.Volume);
        }

        _handles.Clear();
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            try
            {
                Marshal.ReleaseComObject(comObject);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // 已经释放过：忽略
            }
        }
    }

    /// <summary>一条会话的音量控制句柄（按进程聚合后回写用）。</summary>
    private sealed record SessionHandle(uint ProcessId, ISimpleAudioVolume Volume);
}
