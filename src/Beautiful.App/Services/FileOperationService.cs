using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Beautiful.App.Interop;

namespace Beautiful.App.Services;

/// <summary>一次文件操作里的一项：源路径 + 目标完整路径（重命名/新建时为最终名）。</summary>
/// <param name="Source">源路径（删除时即目标本身）。</param>
/// <param name="TargetPath">目标完整路径（复制/移动/重命名）。</param>
public sealed record FileOpItem(string Source, string TargetPath);

/// <summary>进度快照（回调在 UI 线程上，可直接绑定）。</summary>
/// <param name="Completed">已完成项数。</param>
/// <param name="Total">总项数。</param>
/// <param name="CurrentName">当前处理项的名称（来自 Shell 的显示名）。</param>
public sealed record FileOperationProgress(int Completed, int Total, string CurrentName);

/// <summary>操作结果。</summary>
/// <param name="Success">是否整体成功（无失败项且未取消）。</param>
/// <param name="Cancelled">是否被用户中断。</param>
/// <param name="Completed">成功完成的项数。</param>
/// <param name="Failed">失败项数。</param>
/// <param name="Error">失败原因（成功时为 null）。</param>
public sealed record FileOperationResult(bool Success, bool Cancelled, int Completed, int Failed, string? Error)
{
    public static FileOperationResult Ok(int completed) => new(true, false, completed, 0, null);

    public static FileOperationResult Fail(string error, int completed = 0, int failed = 0)
        => new(false, false, completed, failed, error);

    public static FileOperationResult Aborted(int completed) => new(false, true, completed, 0, null);
}

/// <summary>
/// Shell 文件操作服务（Phase 6-c）：复制 / 移动 / 删除（进回收站）/ 重命名 / 新建文件夹。
/// <para>
/// 全部走 <c>IFileOperation</c>，因此天然获得：回收站语义、跨卷移动、长路径、ACL 继承、
/// 冲突处理、以及"系统级的可撤销记录"（<c>FOFX_ADDUNDORECORD</c>）。
/// 本类只负责把调用翻译成 COM 序列，并把 <c>IFileOperationProgressSink</c> 的回调折成
/// <see cref="FileOperationProgress"/>；**不画任何 UI**。
/// </para>
/// <para>
/// 线程模型：<c>IFileOperation</c> 必须在 STA 线程上创建与执行，因此每次操作都在一条独立的
/// 后台 STA 线程上跑，结果通过 <see cref="TaskCompletionSource{TResult}"/> 回传。
/// 进度回调由调用方用 <see cref="Progress{T}"/> 封送（构造于 UI 线程即可自动回 UI）。
/// 取消：Sink 的 <c>Pre*Item</c> 返回 <c>E_ABORT</c>，Shell 会停止后续项——这是官方唯一的软中断手段。
/// </para>
/// <para>
/// 降级：COM 创建失败（极老系统/被安全软件拦截）时，删除改走
/// <see cref="Microsoft.VisualBasic.FileIO.FileSystem"/> 的回收站 API，其余操作返回失败原因，
/// 绝不假装成功。
/// </para>
/// </summary>
public sealed class FileOperationService
{
    private const uint FileAttributeDirectory = 0x10;
    private const int EAbort = unchecked((int)0x80004004);

    /// <summary>复制：<paramref name="items"/> 的 TargetPath 已由 Core 的粘贴/拖放计划算好。</summary>
    public Task<FileOperationResult> CopyAsync(
        IReadOnlyList<FileOpItem> items,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancel = default,
        IntPtr ownerWindow = default)
        => RunAsync(items, progress, cancel, ownerWindow, operation =>
        {
            foreach (var item in items)
            {
                // 逐项 sink 传 null：进度回调由 Advise 注册的那一个接收。
                // 两处都传同一个对象会让每个事件被投递两次（进度翻倍、完成数翻倍）。
                operation.CopyItem(
                    FileOperationInterop.ItemFromPath(item.Source),
                    FileOperationInterop.ItemFromPath(Path.GetDirectoryName(item.TargetPath) ?? item.TargetPath),
                    Path.GetFileName(item.TargetPath),
                    null);
            }
        });

    /// <summary>移动（跨卷时由 Shell 自动退化为复制+删除）。</summary>
    public Task<FileOperationResult> MoveAsync(
        IReadOnlyList<FileOpItem> items,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancel = default,
        IntPtr ownerWindow = default)
        => RunAsync(items, progress, cancel, ownerWindow, operation =>
        {
            foreach (var item in items)
            {
                operation.MoveItem(
                    FileOperationInterop.ItemFromPath(item.Source),
                    FileOperationInterop.ItemFromPath(Path.GetDirectoryName(item.TargetPath) ?? item.TargetPath),
                    Path.GetFileName(item.TargetPath),
                    null);
            }
        });

    /// <summary>删除到回收站（macOS 的"移到废纸篓"语义）。</summary>
    public Task<FileOperationResult> RecycleAsync(
        IReadOnlyList<string> paths,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancel = default,
        IntPtr ownerWindow = default)
    {
        var items = paths.Select(path => new FileOpItem(path, path)).ToList();
        return RunAsync(items, progress, cancel, ownerWindow, operation =>
        {
            foreach (var item in items)
            {
                operation.DeleteItem(FileOperationInterop.ItemFromPath(item.Source), null);
            }
        }, fallbackDelete: paths);
    }

    /// <summary>重命名（同目录内改名，不动内容）。</summary>
    public Task<FileOperationResult> RenameAsync(
        string path,
        string newName,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancel = default,
        IntPtr ownerWindow = default)
    {
        var items = new[] { new FileOpItem(path, newName) };
        return RunAsync(items, progress, cancel, ownerWindow, operation
            => operation.RenameItem(FileOperationInterop.ItemFromPath(path), newName, null));
    }

    /// <summary>新建文件夹（名称冲突由调用方先用 Core 规则避让）。</summary>
    public Task<FileOperationResult> NewFolderAsync(
        string directory,
        string name,
        IntPtr ownerWindow = default)
    {
        var items = new[] { new FileOpItem(directory, name) };
        return RunAsync(items, null, CancellationToken.None, ownerWindow, operation
            => operation.NewItem(FileOperationInterop.ItemFromPath(directory), FileAttributeDirectory, name, null, null));
    }

    // ---------- 执行骨架 ----------

    private static Task<FileOperationResult> RunAsync(
        IReadOnlyList<FileOpItem> items,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancel,
        IntPtr ownerWindow,
        Action<IFileOperation> queue,
        IReadOnlyList<string>? fallbackDelete = null)
    {
        if (items.Count == 0)
        {
            return Task.FromResult(FileOperationResult.Ok(0));
        }

        var completion = new TaskCompletionSource<FileOperationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            IFileOperation? operation = null;
            FileOperationSink? sink = null;
            uint cookie = 0;
            try
            {
                operation = FileOperationInterop.Create();
                sink = new FileOperationSink(items.Count, progress, cancel);
                cookie = operation.Advise(sink);

                // 静默：系统进度对话框会盖住我们自己的进度卡；冲突由我们预判（Core 计划已避让），
                // 所以不需要系统确认框。删除额外打开回收站语义与撤销记录。
                var flags = FileOperationInterop.FofSilent
                            | FileOperationInterop.FofNoConfirmation
                            | FileOperationInterop.FofNoErrorUi
                            | FileOperationInterop.FofNoConfirmMkdir
                            | FileOperationInterop.FofAllowUndo
                            | FileOperationInterop.FofxAddUndoRecord
                            | FileOperationInterop.FofxEarlyFailure;
                operation.SetOperationFlags(flags);
                if (ownerWindow != IntPtr.Zero)
                {
                    operation.SetOwnerWindow((uint)ownerWindow.ToInt64());
                }

                queue(operation);
                operation.PerformOperations();

                var aborted = operation.GetAnyOperationsAborted();
                completion.TrySetResult(aborted || sink.Cancelled
                    ? FileOperationResult.Aborted(sink.Completed)
                    : sink.Failed > 0
                        ? FileOperationResult.Fail("部分项目未能完成（可能被占用或没有权限）",
                            sink.Completed, sink.Failed)
                        : FileOperationResult.Ok(sink.Completed));
            }
            catch (COMException ex) when (sink is { Cancelled: true } || ex.HResult == EAbort)
            {
                completion.TrySetResult(FileOperationResult.Aborted(sink?.Completed ?? 0));
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException
                                          or IOException or NotSupportedException)
            {
                Logger.Warn($"文件操作失败：{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
                var fallback = TryFallback(fallbackDelete, sink, progress);
                completion.TrySetResult(fallback ?? FileOperationResult.Fail(
                    string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message,
                    sink?.Completed ?? 0, sink?.Failed ?? 0));
            }
            finally
            {
                if (operation is not null)
                {
                    try
                    {
                        if (cookie != 0)
                        {
                            operation.Unadvise(cookie);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"文件操作 Unadvise 失败：{ex.GetType().Name}");
                    }

                    if (Marshal.IsComObject(operation))
                    {
                        Marshal.ReleaseComObject(operation);
                    }
                }
            }
        })
        {
            IsBackground = true,
            Name = "Beautiful.FileOperation"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    /// <summary>
    /// 删除的降级路径：<c>Microsoft.VisualBasic</c> 的回收站 API（与资源管理器同一套 SHFileOperation 语义）。
    /// 只在 IFileOperation 完全不可用时使用；任何一项失败都如实计入失败数。
    /// </summary>
    private static FileOperationResult? TryFallback(
        IReadOnlyList<string>? paths,
        FileOperationSink? sink,
        IProgress<FileOperationProgress>? progress)
    {
        if (paths is null || paths.Count == 0)
        {
            return null;
        }

        var completed = 0;
        var failed = 0;
        var lastError = (string?)null;
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                else
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }

                completed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                          or NotSupportedException or OperationCanceledException)
            {
                failed++;
                lastError = ex.Message;
            }

            progress?.Report(new FileOperationProgress(completed + failed, paths.Count, Path.GetFileName(path)));
        }

        Logger.Info($"文件删除走降级路径（VB 回收站 API）：完成 {completed}，失败 {failed}");
        return failed == 0
            ? FileOperationResult.Ok(completed)
            : FileOperationResult.Fail(lastError ?? "部分项目删除失败", completed, failed);
    }

    /// <summary>
    /// 进度接收器：统计完成/失败项，向 UI 报告当前项名称，并在用户取消时用 E_ABORT 中断。
    /// 实现的是 <c>[ComImport]</c> 接口，CLR 会为它生成 CCW，Shell 直接回调进来。
    /// </summary>
    private sealed class FileOperationSink : IFileOperationProgressSink
    {
        private readonly int _total;
        private readonly IProgress<FileOperationProgress>? _progress;
        private readonly CancellationToken _cancel;
        private int _completed;
        private int _failed;
        private string _current = string.Empty;

        public FileOperationSink(int total, IProgress<FileOperationProgress>? progress, CancellationToken cancel)
        {
            _total = Math.Max(1, total);
            _progress = progress;
            _cancel = cancel;
        }

        public bool Cancelled { get; private set; }

        public int Completed => Volatile.Read(ref _completed);

        public int Failed => Volatile.Read(ref _failed);

        public void StartOperations()
        {
        }

        public void FinishOperations(int result)
        {
        }

        public int PreRenameItem(uint flags, IShellItem item, string newName)
            => CheckCancel(item, newName);

        public void PostRenameItem(uint flags, IShellItem item, string newName, int result, IShellItem? created)
            => Count(result, newName);

        public int PreMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName)
            => CheckCancel(item, newName);

        public void PostMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName,
            int result, IShellItem? created)
            => Count(result, newName);

        public int PreCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName)
            => CheckCancel(item, newName);

        public void PostCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName,
            int result, IShellItem? created)
            => Count(result, newName);

        public int PreDeleteItem(uint flags, IShellItem item) => CheckCancel(item, null);

        public void PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created)
            => Count(result, null);

        public int PreNewItem(uint flags, IShellItem destinationFolder, string newName)
            => CheckCancel(destinationFolder, newName);

        public void PostNewItem(uint flags, IShellItem destinationFolder, string newName, string? templateName,
            uint fileAttributes, int result, IShellItem? newItem)
            => Count(result, newName);

        public void UpdateProgress(uint workTotal, uint workSoFar)
        {
            // 总工作量按"项"粒度报告即可：文件内部字节进度对本产品没有意义（进度条按项目数走）。
        }

        public void ResetTimer()
        {
        }

        public void PauseTimer()
        {
        }

        public void ResumeTimer()
        {
        }

        private int CheckCancel(IShellItem? item, string? name)
        {
            if (_cancel.IsCancellationRequested)
            {
                Cancelled = true;
                return EAbort;
            }

            _current = string.IsNullOrEmpty(name) ? DisplayNameOf(item) : name!;
            Report();
            return 0;
        }

        private void Count(int result, string? name)
        {
            if (result < 0)
            {
                Interlocked.Increment(ref _failed);
            }
            else
            {
                Interlocked.Increment(ref _completed);
            }

            if (!string.IsNullOrEmpty(name))
            {
                _current = name!;
            }

            Report();
        }

        private void Report()
            => _progress?.Report(new FileOperationProgress(
                Math.Min(Completed + Failed, _total), _total, _current));

        /// <summary>取 shell 显示名（<c>SIGDN_NORMALDISPLAY</c>）；失败返回空串，绝不让回调抛异常。</summary>
        private static string DisplayNameOf(IShellItem? item)
        {
            if (item is null)
            {
                return string.Empty;
            }

            IntPtr buffer = IntPtr.Zero;
            try
            {
                item.GetDisplayName(0, out buffer);
                return buffer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(buffer) ?? string.Empty;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                return string.Empty;
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(buffer);
                }
            }
        }
    }
}
