using System.IO;
using System.Linq;
using Beautiful.Core.Animation;
using Beautiful.Core.Settings;

namespace Beautiful.App.Services;

/// <summary>
/// 动画参数的热加载宿主：<c>config/anim-params.json</c> ↔ <see cref="AnimParams"/>。
/// <para>
/// 【为什么要一个 Store 而不是各处读设置】参数是"调观感"用的，改一次要立刻看到效果：AnimLab 里拖完
/// 参数写文件，产品这边下一次最小化就该用新值。<see cref="FileSystemWatcher"/> 监听 + 防抖 150ms
/// 重读，重读失败保留上一份可用值（<b>配置文件写坏不能让动画停摆</b>）。
/// </para>
/// <para>
/// 【为什么文件不存在时主动写一份】参数表如果只存在于代码里，用户永远不知道有哪些旋钮可调。
/// 首次运行落一份带默认值的 JSON，等于把"调参指南"直接放到他手边。
/// </para>
/// </summary>
public static class AnimParamsStore
{
    private static readonly object Gate = new();
    private static AnimParams _current = AnimParams.Default;
    private static string? _directory;
    private static FileSystemWatcher? _watcher;
    private static DateTime _lastReload = DateTime.MinValue;

    /// <summary>当前生效参数（读取无锁，写入在锁内整体替换引用——引用赋值对读线程是原子可见的）。</summary>
    public static AnimParams Current => _current;

    /// <summary>
    /// 参数热加载/保存后触发（参数为<b>已生效</b>的那份）。alpha.15 接线：
    /// <see cref="Beautiful.Core.Animation.AnimPolicy"/> 持有的是构造时快照，
    /// 不订阅这个事件的话，改 anim-params.json 里的时长永远不生效（alpha.14 的实际缺陷）。
    /// 触发自任意线程（FileSystemWatcher/AnimLab），订阅方自己负责 marshal。
    /// </summary>
    public static event Action<AnimParams>? ParamsChanged;

    /// <summary>参数文件完整路径（诊断行与 AnimLab 共用）。</summary>
    public static string FilePath =>
        Path.Combine(_directory ?? Path.Combine(AppContext.BaseDirectory, "config"), "anim-params.json");

    /// <summary>
    /// 初始化：确保目录/文件存在 → 读一次 → 起监听。<paramref name="directory"/> 为空时用
    /// <c>AppContext.BaseDirectory\config</c>（与 settings.json 同一目录）。
    /// </summary>
    public static void Load(string? directory = null)
    {
        lock (Gate)
        {
            _directory = directory ?? Path.Combine(AppContext.BaseDirectory, "config");
            try
            {
                Directory.CreateDirectory(_directory);
                if (!File.Exists(FilePath))
                {
                    File.WriteAllText(FilePath, AnimParams.Default.ToJson());
                    Logger.Info($"动画参数：已生成默认参数表 {FilePath}");
                }

                var text = File.ReadAllText(FilePath);
                var parsed = AnimParams.FromJson(text, out var error);
                _current = parsed;
                if (error is null)
                    Logger.Info($"动画参数：已加载 {_current.ToLogLine()}");
                else
                    Logger.Warn($"动画参数：{FilePath} 解析失败（{error}），本次使用默认值");
                RaiseParamsChanged();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _current = AnimParams.Default;
                Logger.Warn($"动画参数：读取失败（{ex.GetType().Name}），使用默认值");
            }

            StartWatcher();
        }
    }

    /// <summary>手动重读（AnimLab 写文件后立刻生效，不必等 watcher）。</summary>
    public static AnimParams Reload()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return _current;
                var parsed = AnimParams.FromJson(File.ReadAllText(FilePath), out var error);
                if (error is null)
                {
                    _current = parsed;
                    Logger.Info($"动画参数：热加载 {_current.ToLogLine()}");
                    RaiseParamsChanged();
                }
                else
                {
                    Logger.Warn($"动画参数：热加载失败（{error}），继续使用上一份");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Logger.Warn($"动画参数：热加载读取失败（{ex.GetType().Name}），继续使用上一份");
            }
            return _current;
        }
    }

    /// <summary>把当前参数写回文件（AnimLab 的"保存"与"一键导出诊断包"用）。</summary>
    public static void Save(AnimParams parameters)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(_directory ?? Path.Combine(AppContext.BaseDirectory, "config"));
                File.WriteAllText(FilePath, parameters.Normalized().ToJson());
                _current = parameters.Normalized();
                Logger.Info($"动画参数：已保存 {_current.ToLogLine()}");
                RaiseParamsChanged();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Logger.Warn($"动画参数：保存失败（{ex.GetType().Name}）：{ex.Message}");
            }
        }
    }

    private static void StartWatcher()
    {
        if (_watcher is not null || _directory is null) return;
        try
        {
            _watcher = new FileSystemWatcher(_directory, "anim-params.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            // 编辑器保存会连发多次（写临时文件 + 改名）：150ms 防抖 + 只认一次
            _watcher.Changed += (_, _) => DebouncedReload();
            _watcher.Created += (_, _) => DebouncedReload();
            _watcher.Renamed += (_, _) => DebouncedReload();
            Logger.Info($"动画参数：已监听 {FilePath}（改文件即热加载）");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            Logger.Warn($"动画参数：监听失败（{ex.GetType().Name}），只能用 AnimLab 手动重载");
        }
    }

    private static void DebouncedReload()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastReload).TotalMilliseconds < 150) return;
        _lastReload = now;
        Reload();
    }

    /// <summary>锁内触发（订阅方异常只记日志，不让一个坏订阅拖垮热加载）。</summary>
    private static void RaiseParamsChanged()
    {
        var handlers = ParamsChanged;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<AnimParams>>())
        {
            try { handler(_current); }
            catch (Exception ex)
            {
                Logger.Warn($"动画参数：订阅方处理异常（已隔离）：{ex.GetType().Name} {ex.Message}");
            }
        }
    }
}
