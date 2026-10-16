using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Dock;

namespace Beautiful.App.Dock;

/// <summary>
/// Dock 数据源：合并 固定应用（PinnedAppsStore → config/dock.json，缺失时代码默认）
/// + WindowTracker 运行窗口（实时增删）+ 回收站 + 媒体胶囊占位，并按主题插入**分隔符**条目。
/// <para>
/// 语义序列（不含分隔符）固定为：Pinned… → Window… → 回收站 → 媒体。分隔符不是一份第二套数据，
/// 而是**每次由 Core 的 <see cref="DockSeparatorPlan"/> 从语义序列推导出来**（macOS 主题开、其余关），
/// 所以窗口块增删、固定块重载、主题切换三条路径都不会让细线插错位置或插两道。
/// </para>
/// <para>
/// 写回 <see cref="Items"/> 走"公共前缀/后缀对齐"的最小差异：只有真正变化的区段才发集合事件，
/// 标题刷新仍发 Replace（<see cref="DockSurface"/> 的快路径只换 DataContext、不重建可视树）。
/// 分隔符用固定实例（每个角色一个），位置没变就不会产生集合事件。
/// </para>
/// 所有事件均在 UI 线程到达（WindowTracker 保证），无需加锁。
/// </summary>
public sealed class DockViewModel
{
    private readonly WindowTracker _tracker;
    private readonly PinnedAppsStore _pinnedStore;

    /// <summary>语义块：固定项（dock.json 现值）。</summary>
    private readonly List<DockItem> _pinned = new();

    /// <summary>语义块：运行窗口，与 <see cref="WindowTracker.Windows"/> 下标一一对应。</summary>
    private readonly List<DockItem> _windows = new();

    // 后缀槽各一个固定实例：回收站 + 媒体胶囊（胶囊无会话时由模板把宽度收成 0）
    private readonly DockItem _recycleBin = CreateRecycleBinItem();
    private readonly DockItem _media = CreateMediaItem();

    // 分隔符同样是固定实例（角色 → 实例）：槽位不变时 Reconcile 不产生任何集合事件
    private readonly DockItem _appsSeparator = MakeSeparator(DockSeparatorRole.AppsBoundary);
    private readonly DockItem _trashSeparator = MakeSeparator(DockSeparatorRole.TrashBoundary);

    private bool _separatorsEnabled;

    /// <summary>最近一次落到 Items 的分隔符槽位（语义下标 → 实际下标的换算依据）。</summary>
    private IReadOnlyList<DockSeparatorSlot> _slots = Array.Empty<DockSeparatorSlot>();

    public ObservableCollection<DockItem> Items { get; } = new();

    /// <param name="separatorsEnabled">初始分隔符开关（来自主题 DockSeparatorsEnabled；主题切换经 <see cref="ApplySeparatorPolicy"/> 重放）。</param>
    /// <param name="importTaskbarPins">首次建列表时是否并入 Windows 原生任务栏的固定应用（已有 dock.json 则不动）。</param>
    public DockViewModel(WindowTracker tracker, PinnedAppsStore? pinnedStore = null, bool separatorsEnabled = false,
        bool importTaskbarPins = false)
    {
        _tracker = tracker;
        _pinnedStore = pinnedStore ?? new PinnedAppsStore();
        _separatorsEnabled = separatorsEnabled;

        if (importTaskbarPins)
            _pinnedStore.ImportIfFirstRun(Services.TaskbarPinsReader.Read(), Services.TaskbarPinsReader.DedupKeyOf);

        foreach (var path in _pinnedStore.Load()) _pinned.Add(MakePinned(path));
        foreach (var w in tracker.Windows)
        {
            w.PropertyChanged += OnWindowItemPropertyChanged;
            _windows.Add(ToDockItem(w));
        }
        Reconcile();

        tracker.Windows.CollectionChanged += OnWindowsChanged;
    }

    /// <summary>解除全部事件订阅（DockWindow 关闭时调用，随后 tracker.Dispose）。</summary>
    public void Detach()
    {
        _tracker.Windows.CollectionChanged -= OnWindowsChanged;
        UnhookWindowItems();
    }

    // ---------- 主题：分隔符策略 ----------

    /// <summary>
    /// 主题落地分隔符开关（macOS 开、GNOME/鸿蒙关）：变化时重排 Items。
    /// 【为什么由主题而不是渲染器决定】"原生 Dock 有没有分隔符"是主题参数，不是 if (theme == mac)；
    /// 插在哪一段之间由 Core 规则算，主题只给"要不要"。
    /// </summary>
    public void ApplySeparatorPolicy(bool enabled)
    {
        if (_separatorsEnabled == enabled) return;
        _separatorsEnabled = enabled;
        Reconcile();
    }

    // ---------- 固定项 / 占位项 ----------

    /// <summary>
    /// 按 dock.json 现值重建固定块（设置窗口编辑 / 右键固定变更后调用）：固定块整体换实例后，
    /// 分隔符槽位（"固定→运行"边界）可能整体平移甚至消失，故一律走 <see cref="Reconcile"/> 重导。
    /// </summary>
    public void ReloadPinned()
    {
        _pinned.Clear();
        foreach (var path in _pinnedStore.Load()) _pinned.Add(MakePinned(path));
        Reconcile();
    }

    /// <summary>
    /// 运行窗口块（与 <see cref="WindowTracker.Windows"/> 下标一一对应）。右键菜单要按"同一个可执行文件 /
    /// 同一个进程"把该应用的其它窗口找出来，数据源只有这一块——暴露只读视图，不暴露可写引用。
    /// </summary>
    public IReadOnlyList<DockItem> RunningWindows => _windows;

    /// <summary>该路径所代表的应用是否已在固定列表（.lnk 与其目标 exe 视为同一个应用，与固定判重同源）。</summary>
    public bool IsPinnedPath(string path) =>
        DockPinOps.IndexOfTarget(_pinnedStore.Load(), ResolveTarget(path), ResolveTarget) >= 0;

    /// <summary>将 exe 路径加入固定列表（已固定则无操作），写回 dock.json 并重载固定块。</summary>
    public void PinExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var paths = _pinnedStore.Load();
        // 按"解析后的目标"判重：.lnk 与其目标 exe 视为同一个应用，不再各占一格（旧的按原串 Contains 会漏判）。
        if (DockPinOps.IndexOfTarget(paths, ResolveTarget(path), ResolveTarget) >= 0) return;
        paths.Add(path);
        _pinnedStore.Save(paths);
        ReloadPinned();
    }

    /// <summary>取消固定（按路径移除），写回 dock.json 并重载固定块；路径不在列表时无操作。</summary>
    public void Unpin(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var paths = _pinnedStore.Load();
        if (!DockPinOps.Remove(paths, path, ResolveTarget)) return;   // 用目标匹配：右键/拖出取消固定能删掉 .lnk 写法那条
        _pinnedStore.Save(paths);
        ReloadPinned();
    }

    // ---------- 拖放落位（外部拖入 / 内部排序 / 甩出取消固定）----------

    /// <summary>固定块当前项数（DockSurface 的槽位坐标系与 dock.json 顺序一一对应，此值是钳制上界）。</summary>
    public int PinnedCount => _pinned.Count;

    /// <summary>
    /// 外部拖入的落位裁定结果（供 DockWindow 写日志 + 决定是否 no-op，渲染器不关心这些语义）。
    /// </summary>
    public enum PinDropResult { Inserted, MovedExisting, Rejected }

    /// <summary>
    /// 把拖来的文件在固定块 slot 处固定。<paramref name="slot"/> 为固定块插入槽（越界由 Core 钳制）。
    /// 规则：不可固定的源（文件夹/非 .exe .lnk）→ Rejected 带原因；目标已固定 → 把既有条目移到落点
    /// （MovedExisting，绝不加第二条）；否则插入（Inserted）。全部写回 dock.json 并重载。
    /// </summary>
    public PinDropResult DropPinAt(string filePath, int slot, out string reason)
    {
        reason = string.Empty;
        if (!CanPin(filePath, out reason)) return PinDropResult.Rejected;

        var paths = _pinnedStore.Load();
        var idx = DockPinOps.Insert(paths, filePath, slot, ResolveTarget, out var added);
        if (!added) DockPinOps.MoveToSlot(paths, idx, slot);   // 重复：挪到落点高亮，而不是堆第二条
        _pinnedStore.Save(DockPinOps.Normalize(paths, ResolveTarget));
        ReloadPinned();
        return added ? PinDropResult.Inserted : PinDropResult.MovedExisting;
    }

    /// <summary>固定块内部排序：把 from 项移到 slot。from 非法返回 false（不改表）。</summary>
    public bool MovePinned(int from, int slot)
    {
        var paths = _pinnedStore.Load();
        if (!DockPinOps.MoveToSlot(paths, from, slot)) return false;
        _pinnedStore.Save(paths);
        ReloadPinned();
        return true;
    }

    /// <summary>
    /// 判定一个拖放源能否固定：仅已存在的 .exe / .lnk 可固定。文件夹 / 其它扩展名 / 不存在 → 拒绝并给原因
    /// （Finder-style 文件夹格本模型不支持，宁可拒绝也不"静默钉一个启动即坏的条目"）。
    /// </summary>
    public static bool CanPin(string path, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) { reason = "空路径"; return false; }
        try
        {
            if (Directory.Exists(path)) { reason = $"文件夹不能固定到 Dock：{path}"; return false; }
            if (!File.Exists(path)) { reason = $"文件不存在：{path}"; return false; }
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".exe" or ".lnk") return true;
            reason = $"仅支持固定 .exe / .lnk，收到“{(ext.Length == 0 ? "无扩展名" : ext)}”：{path}";
            return false;
        }
        catch (Exception ex) { reason = ex.Message; return false; }
    }

    /// <summary>
    /// 解析 dock.json 条目 / 拖放文件到"去重用的目标"：.lnk 取其真实目标 exe，其余原样。
    /// internal：右键菜单的"这个应用是不是已经在固定块里"必须用**同一把尺子**，另写一份就会漂移
    /// （本仓库的固定/取消固定反复出现"删不掉那一条"就是两套判重不一致的代价）。
    /// </summary>
    internal static string ResolveTarget(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return path;
        try
        {
            var target = ShellLink.Resolve(path).TargetPath;
            return string.IsNullOrEmpty(target) ? path : target;
        }
        catch { return path; }   // 坏快捷方式退回原路径（DockPinOps 亦会 try/catch，双保险不丢项）
    }

    private static DockItem MakePinned(string path)
    {
        // 图标源与启动路径分家：.lnk 直接按路径取图在本机取不到（SHDefExtractIconW 返回 hr=1 空句柄），
        // 必须解析到目标 exe / IconLocation 才有 256px 的大帧可用（v3 T6 实测）。
        var iconSource = IconSourceFor(path);
        return new DockItem(Id: path,
                            Label: Path.GetFileNameWithoutExtension(path),
                            Icon: IconExtractor.GetIcon(iconSource),
                            Kind: DockItemKind.Pinned,
                            LaunchPath: path,
                            IconSource: iconSource);
    }

    /// <summary>
    /// 固定项图标源：.lnk 经 ShellLink 解析取 IconLocation（剥去尾随 ",index"），无自定义图标
    /// 或该文件不存在时回退 TargetPath；两者皆不可用时保留 .lnk 原路径（视图回退占位字形）。
    /// 非 .lnk 原样返回。
    /// </summary>
    private static string IconSourceFor(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return path;
        try
        {
            var resolved = ShellLink.Resolve(path);
            var candidates = new[] { StripIconIndex(resolved.IconLocation), resolved.TargetPath };
            return candidates.FirstOrDefault(File.Exists) ?? path;
        }
        catch
        {
            return path; // 解析失败不阻断固定项创建
        }

        static string StripIconIndex(string loc)
        {
            var comma = loc.LastIndexOf(',');
            return comma > 0 ? loc[..comma] : loc;
        }
    }

    private static DockItem CreateRecycleBinItem()
        => new(Id: "shell:RecycleBinFolder",
               Label: "回收站",
               Icon: IconExtractor.GetIcon(RecycleBinIconSource), // 可能为 null → 视图回退 ♻
               Kind: DockItemKind.RecycleBin,
               LaunchPath: "shell:RecycleBinFolder",
               IconSource: RecycleBinIconSource);

    /// <summary>回收站图标的 shell 命名空间 id（启动路径是 shell: 形式，取图要用 ::{clsid} 形式）。</summary>
    private const string RecycleBinIconSource = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    private static DockItem CreateMediaItem()
        => new(Id: "media", Label: "媒体", Icon: null, Kind: DockItemKind.Media, LaunchPath: string.Empty);

    /// <summary>
    /// 分隔符条目：无图标、无启动路径，Id 带角色（同一角色永远同一个实例，见字段注释）。
    /// Label 留空——它不进工具提示（不悬停、不命中）。
    /// </summary>
    private static DockItem MakeSeparator(DockSeparatorRole role)
        => new(Id: "separator:" + role, Label: string.Empty, Icon: null,
               Kind: DockItemKind.Separator, LaunchPath: string.Empty);

    private DockItem SeparatorFor(DockSeparatorRole role)
        => role == DockSeparatorRole.AppsBoundary ? _appsSeparator : _trashSeparator;

    // ---------- 运行窗口（实时增删 + 标题刷新） ----------

    private static DockItem ToDockItem(WindowItem w)
        => new(Id: w.Handle.ToString(),
               Label: w.Title,
               Icon: w.Icon,
               Kind: DockItemKind.Window,
               LaunchPath: w.ExePath,
               WindowHandle: w.Handle,
               IconSource: w.ExePath);   // 跟踪器就是按 exe 路径取图并缓存的，键与它同源

    private void OnWindowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems is null) break;
                for (var i = 0; i < e.NewItems.Count; i++)
                {
                    var w = (WindowItem)e.NewItems[i]!;
                    w.PropertyChanged += OnWindowItemPropertyChanged;
                    // _windows 与 tracker.Windows 下标对齐（与固定块在 Items 里的位置无关）
                    var at = Math.Clamp(e.NewStartingIndex + i, 0, _windows.Count);
                    _windows.Insert(at, ToDockItem(w));
                }
                Reconcile();
                break;

            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems is null) break;
                for (var i = e.OldItems.Count - 1; i >= 0; i--)
                {
                    var w = (WindowItem)e.OldItems[i]!;
                    w.PropertyChanged -= OnWindowItemPropertyChanged;
                    var idx = e.OldStartingIndex + i;
                    if (idx < _windows.Count) _windows.RemoveAt(idx);
                }
                Reconcile();
                break;

            case NotifyCollectionChangedAction.Replace:
                if (e.OldItems is null || e.NewItems is null) break;
                {
                    var old = (WindowItem)e.OldItems[0]!;
                    old.PropertyChanged -= OnWindowItemPropertyChanged;
                    var replacement = (WindowItem)e.NewItems[0]!;
                    replacement.PropertyChanged += OnWindowItemPropertyChanged;
                    var idx = e.NewStartingIndex;
                    if (idx < _windows.Count) _windows[idx] = ToDockItem(replacement);
                }
                Reconcile();
                break;

            case NotifyCollectionChangedAction.Move:
                Reconcile();   // 顺序由 tracker.Windows 决定，语义块跟着它的下标走，这里只需重导
                break;

            case NotifyCollectionChangedAction.Reset:
                UnhookWindowItems();
                _windows.Clear();
                foreach (var w in _tracker.Windows)
                {
                    w.PropertyChanged += OnWindowItemPropertyChanged;
                    _windows.Add(ToDockItem(w));
                }
                Reconcile();
                break;
        }
    }

    private void OnWindowItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WindowItem.Title) || sender is not WindowItem w) return;
        var i = _windows.FindIndex(it => it.WindowHandle == w.Handle);
        if (i < 0) return;
        var replacement = ToDockItem(w); // record 不可变 → 原位替换新实例
        _windows[i] = replacement;
        // 仍走 Replace（不是先删后插）：DockSurface 的 Replace 快路径只换 DataContext，避免整树重建闪烁
        var real = ToRealIndex(_pinned.Count + i);
        if (real >= 0 && real < Items.Count && Items[real].Kind == DockItemKind.Window) Items[real] = replacement;
        else Reconcile();
    }

    private void UnhookWindowItems()
    {
        foreach (var w in _tracker.Windows) w.PropertyChanged -= OnWindowItemPropertyChanged;
    }

    // ---------- 组合：语义序列 → Items（插入分隔符 + 最小差异写回） ----------

    /// <summary>
    /// 语义下标（不含分隔符的序列位置）→ <see cref="Items"/> 实际下标：
    /// 加上所有"插在本位置之前或本位置"的槽位数。槽位下标是语义坐标系里的，故 ≤ 本下标的都算。
    /// </summary>
    private int ToRealIndex(int semanticIndex)
    {
        var real = semanticIndex;
        foreach (var s in _slots)
            if (s.Index <= semanticIndex) real++;
        return real;
    }

    /// <summary>
    /// 由三个语义块 + 后缀槽重导 Items：算槽位 → 生目标序列 → 与现有 Items 做前缀/后缀对齐，
    /// 只把中间差异段删插。集合事件数 = 真正变化的条目数（运行窗口增删时恒 1）。
    /// </summary>
    private void Reconcile()
    {
        // ① 语义序列（无分隔符）
        var semantic = new List<DockItem>(_pinned.Count + _windows.Count + 2);
        var kinds = new List<DockItemKind>(semantic.Count);
        AddBlock(semantic, kinds, _pinned);
        AddBlock(semantic, kinds, _windows);
        semantic.Add(_recycleBin); kinds.Add(_recycleBin.Kind);
        semantic.Add(_media); kinds.Add(_media.Kind);

        // ② 主题决定的分隔符槽位（Core 纯函数，可单测）
        _slots = DockSeparatorPlan.Slots(kinds, _separatorsEnabled);
        var target = new List<DockItem>(semantic.Count + _slots.Count);
        var at = 0;
        foreach (var s in _slots)
        {
            while (at < s.Index) target.Add(semantic[at++]);
            target.Add(SeparatorFor(s.Role));
        }
        while (at < semantic.Count) target.Add(semantic[at++]);

        // ③ 最小差异写回（引用相等比较：条目实例与分隔符实例都固定，未动的区段零事件）
        var common = 0;
        while (common < Items.Count && common < target.Count && ReferenceEquals(Items[common], target[common])) common++;
        var suffix = 0;
        while (suffix < Items.Count - common && suffix < target.Count - common &&
               ReferenceEquals(Items[Items.Count - 1 - suffix], target[target.Count - 1 - suffix])) suffix++;

        for (var i = Items.Count - common - suffix; i > 0; i--) Items.RemoveAt(common);
        for (var i = common; i < target.Count - suffix; i++) Items.Insert(i, target[i]);
    }

    private static void AddBlock(List<DockItem> target, List<DockItemKind> kinds, List<DockItem> block)
    {
        foreach (var item in block)
        {
            target.Add(item);
            kinds.Add(item.Kind);
        }
    }
}
