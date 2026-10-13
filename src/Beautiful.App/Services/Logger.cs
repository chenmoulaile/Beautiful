using System.IO;

namespace Beautiful.App.Services;

/// <summary>
/// 极简文件日志（Task 17 壳层）。写入 <c>AppContext.BaseDirectory/logs/Beautiful-yyyyMM.log</c>：
/// 追加模式，按月滚动（文件名含月份，跨月自然换文件）；单文件超 5MB 时改名为 .old（覆盖旧 .old）。
/// 线程安全：全局 lock 串行化写入。启动流程中 <see cref="Init"/> 最先调用；未初始化或写入失败时
/// 全部方法静默吞掉异常——日志系统自身绝不能成为崩溃源（fire-and-forget 契约）。
/// </summary>
public static class Logger
{
    private const long MaxBytes = 5 * 1024 * 1024; // 单文件上限 5MB，超过滚动为 .old
    private static readonly object Lock = new();
    private static string? _dir; // null = 未初始化 → 所有写入为 no-op

    /// <summary>
    /// 初始化日志目录（BaseDirectory/logs）。幂等；目录创建失败时保持未初始化（全部写入静默跳过）。
    /// </summary>
    public static void Init()
    {
        if (_dir is not null) return;
        try
        {
            _dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(_dir);
        }
        catch
        {
            _dir = null; // 无法落盘（如安装目录只读）：整体降级为无日志
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var dir = _dir;
        if (dir is null) return;
        try
        {
            lock (Lock)
            {
                var path = Path.Combine(dir, $"Beautiful-{DateTime.Now:yyyyMM}.log");
                RotateIfNeeded(path);
                File.AppendAllText(path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志写入失败（磁盘满/权限/句柄竞争等）：静默降级，绝不外抛
        }
    }

    /// <summary>超 5MB → 改名 .old（覆盖旧 .old）。改名失败（如被占用）时放弃本次滚动，继续追加。</summary>
    private static void RotateIfNeeded(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > MaxBytes)
                File.Move(path, path + ".old", overwrite: true);
        }
        catch
        {
            // 滚动失败不阻断本次写入
        }
    }
}
