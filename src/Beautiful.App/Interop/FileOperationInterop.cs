using System;
using System.Runtime.InteropServices;

namespace Beautiful.App.Interop;

/// <summary>
/// Shell 文件操作的最小互操作面（Phase 6-c）：<c>IFileOperation</c> + <c>IShellItem</c> +
/// <c>IFileOperationProgressSink</c>。
/// <para>
/// 为什么不用 <c>File.Copy</c>/<c>File.Delete</c>：本仓库要的是**系统级语义**——回收站、长路径、
/// 冲突/提权、ACL 继承、大目录进度与可中断。这些只有 Shell 的文件操作引擎给得起；
/// 自己拼 File.* 会在"跨卷移动/只读文件/被占用文件/回收站"四处翻车，而且没有撤销记录。
/// </para>
/// <para>
/// 接口方法顺序必须与 <c>shobjidl_core.h</c> 的 vtable 严格一致（少一个/错一个就是
/// <c>AccessViolationException</c> 或静默调用到错误槽位），因此每个接口都按头文件逐条抄写并注明来源。
/// 所有 HRESULT 都按默认行为（失败抛 <see cref="COMException"/>）——调用方统一 try/catch 收口。
/// </para>
/// </summary>
internal static class FileOperationInterop
{
    // ---- 操作标志（shobjidl_core.h） ----
    public const uint FofSilent = 0x0004;
    public const uint FofNoConfirmation = 0x0010;
    public const uint FofAllowUndo = 0x0040;         // 删除 → 回收站
    public const uint FofNoErrorUi = 0x0400;
    public const uint FofNoConfirmMkdir = 0x0200;
    public const uint FofxAddUndoRecord = 0x20000000;
    public const uint FofxEarlyFailure = 0x00100000;

    private static readonly Guid ClsidFileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    private static readonly Guid IidFileOperation = new("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8");
    private static readonly Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    private const uint ClsctxAll = 0x17;   // CLSCTX_INPROC_SERVER | LOCAL_SERVER | REMOTE_SERVER

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint clsctx, ref Guid iid,
        out IntPtr instance);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid riid,
        out IShellItem item);

    /// <summary>创建 <c>IFileOperation</c> 实例（必须在该线程的 STA 公寓里使用）。</summary>
    /// <exception cref="COMException">系统不支持/创建失败。</exception>
    public static IFileOperation Create()
    {
        var clsid = ClsidFileOperation;
        var iid = IidFileOperation;
        var hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxAll, ref iid, out var instance);
        if (hr < 0 || instance == IntPtr.Zero)
        {
            throw new COMException("无法创建 IFileOperation 实例。", hr);
        }

        try
        {
            return (IFileOperation)Marshal.GetObjectForIUnknown(instance);
        }
        finally
        {
            Marshal.Release(instance);
        }
    }

    /// <summary>路径 → <c>IShellItem</c>（不存在的路径会失败，调用方应提前判存在）。</summary>
    /// <exception cref="COMException">路径无法解析为 shell 项。</exception>
    public static IShellItem ItemFromPath(string path)
    {
        var iid = IidShellItem;
        var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item);
        if (hr < 0 || item is null)
        {
            throw new COMException($"无法解析 shell 项：{path}", hr);
        }

        return item;
    }
}

/// <summary>
/// <c>IFileOperation</c>（shobjidl_core.h）。方法顺序即 vtable 顺序，勿调整。
/// </summary>
[ComImport]
[Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperation
{
    uint Advise([MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink sink);

    void Unadvise(uint cookie);

    void SetOperationFlags(uint flags);

    void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);

    void SetProgressDialog([MarshalAs(UnmanagedType.Interface)] object progressDialog);

    void SetProperties([MarshalAs(UnmanagedType.Interface)] object propertyChangeArray);

    void SetOwnerWindow(uint owner);

    void ApplyPropertiesToItem([MarshalAs(UnmanagedType.Interface)] IShellItem item);

    void ApplyPropertiesToItems([MarshalAs(UnmanagedType.Interface)] object items);

    void RenameItem([MarshalAs(UnmanagedType.Interface)] IShellItem item,
        [MarshalAs(UnmanagedType.LPWStr)] string newName,
        [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? sink);

    void RenameItems([MarshalAs(UnmanagedType.Interface)] object items,
        [MarshalAs(UnmanagedType.LPWStr)] string newName);

    void MoveItem([MarshalAs(UnmanagedType.Interface)] IShellItem item,
        [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string? newName,
        [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? sink);

    void MoveItems([MarshalAs(UnmanagedType.Interface)] object items,
        [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder);

    void CopyItem([MarshalAs(UnmanagedType.Interface)] IShellItem item,
        [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string? copyName,
        [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? sink);

    void CopyItems([MarshalAs(UnmanagedType.Interface)] object items,
        [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder);

    void DeleteItem([MarshalAs(UnmanagedType.Interface)] IShellItem item,
        [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? sink);

    void DeleteItems([MarshalAs(UnmanagedType.Interface)] object items);

    void NewItem([MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder, uint fileAttributes,
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        [MarshalAs(UnmanagedType.LPWStr)] string? templateName,
        [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? sink);

    void PerformOperations();

    [return: MarshalAs(UnmanagedType.Bool)]
    bool GetAnyOperationsAborted();
}

/// <summary><c>IShellItem</c>（shobjidl_core.h）。本仓库只用到路径解析与显示名。</summary>
[ComImport]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    void BindToHandler(IntPtr bindCtx, ref Guid handler, ref Guid riid, out IntPtr result);

    void GetParent(out IShellItem parent);

    void GetDisplayName(uint sigdnName, out IntPtr name);

    void GetAttributes(uint mask, out uint attributes);

    void Compare(IShellItem other, uint hint, out int order);
}

/// <summary>
/// <c>IFileOperationProgressSink</c>（shobjidl_core.h）。本仓库用它做三件事：
/// 统计已完成项数（进度条）、拿到当前项名称（进度卡文案）、在用户点"停止"时返回 E_ABORT 中断。
/// 方法顺序即 vtable 顺序，勿调整。
/// </summary>
[ComImport]
[Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperationProgressSink
{
    void StartOperations();

    void FinishOperations(int result);

    // Pre* 用 PreserveSig 返回 HRESULT：这是唯一能中断操作的官方手段（返回 E_ABORT）。
    // 其余方法声明为 void，等价于"总是返回 S_OK"，本仓库也只用来计数。
    [PreserveSig]
    int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName);

    void PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName,
        int result, IShellItem? newlyCreated);

    [PreserveSig]
    int PreMoveItem(uint flags, IShellItem item, IShellItem destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string? newName);

    void PostMoveItem(uint flags, IShellItem item, IShellItem destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, IShellItem? newlyCreated);

    [PreserveSig]
    int PreCopyItem(uint flags, IShellItem item, IShellItem destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string? newName);

    void PostCopyItem(uint flags, IShellItem item, IShellItem destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, IShellItem? newlyCreated);

    [PreserveSig]
    int PreDeleteItem(uint flags, IShellItem item);

    void PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newlyCreated);

    [PreserveSig]
    int PreNewItem(uint flags, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string newName);

    void PostNewItem(uint flags, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string newName,
        [MarshalAs(UnmanagedType.LPWStr)] string? templateName, uint fileAttributes, int result,
        IShellItem? newItem);

    void UpdateProgress(uint workTotal, uint workSoFar);

    void ResetTimer();

    void PauseTimer();

    void ResumeTimer();
}
