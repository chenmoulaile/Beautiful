using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using Beautiful.App.Interop;
using Beautiful.App.Services;

namespace Beautiful.App.Dock;

/// <summary>
/// Dock 数据源：合并 固定应用（PinnedAppsStore → config/dock.json，缺失时代码默认）
/// + WindowTracker 运行窗口（实时增删）+ 回收站 + 媒体胶囊占位。
/// 布局顺序固定为：Pinned… | Window… | 回收站 | 媒体；窗口块始终连续，映射下标 = 固定项数 + 跟踪集合下标。
/// 所有事件均在 UI 线程到达（WindowTracker 保证），无需加锁。
/// </summary>
public sealed class DockViewModel
{
    private readonly WindowTracker _tracker;
    private readonly PinnedAppsStore _pinnedStore;
    private int _pinnedCount; // 可变：ReloadPinned 重建固定块后更新（窗口块映射下标随之平移）

    public ObservableCollection<DockItem> Items { get; } = new();

    public DockViewModel(WindowTracker tracker, PinnedAppsStore? pinnedStore = null)
    {
        _tracker = tracker;
        _pinnedStore = pinnedStore ?? new PinnedAppsStore();

        foreach (var path in _pinnedStore.Load()) Items.Add(MakePinned(path));
        _pinnedCount = Items.Count;

        // 先放回收站/媒体占位，再插入窗口：保证下方 Items.Count - SuffixCount 在构造期
        // 也恒指向固定块之后的窗口区起点（构造期窗口集合即可能非空，索引不能落入固定块）。
        Items.Add(CreateRecycleBinItem());
        Items.Add(CreateMediaItem());

        foreach (var w in tracker.Windows) InsertWindowItem(Items.Count - SuffixCount, w);

        tracker.Windows.CollectionChanged += OnWindowsChanged;
        // 初始窗口的 PropertyChanged 已在上方 InsertWindowItem 内逐个挂接，勿再 HookWindowItems 重复订阅
    }

    /// <summary>解除全部事件订阅（DockWindow 关闭时调用，随后 tracker.Dispose）。</summary>
    public void Detach()
    {
        _tracker.Windows.CollectionChanged -= OnWindowsChanged;
        UnhookWindowItems();
    }

    // ---------- 固定项 / 占位项 ----------

    /// <summary>
    /// 按 dock.json 现值重建固定块（设置窗口编辑 / 右键固定变更后调用）：
    /// 移除旧固定块后按下标 0..n 插入新块，窗口块整体后移保持连续。
    /// </summary>
    public void ReloadPinned()
    {
        var paths = _pinnedStore.Load();
        for (var i = _pinnedCount - 1; i >= 0; i--) Items.RemoveAt(i);
        for (var i = 0; i < paths.Count; i++) Items.Insert(i, MakePinned(paths[i]));
        _pinnedCount = paths.Count;
    }

    /// <summary>将 exe 路径加入固定列表（已固定则无操作），写回 dock.json 并重载固定块。</summary>
    public void PinExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var paths = _pinnedStore.Load();
        if (paths.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        paths.Add(path);
        _pinnedStore.Save(paths);
        ReloadPinned();
    }

    /// <summary>取消固定（按路径移除），写回 dock.json 并重载固定块；路径不在列表时无操作。</summary>
    public void Unpin(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var paths = _pinnedStore.Load();
        var next = paths.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (next.Count == paths.Count) return;
        _pinnedStore.Save(next);
        ReloadPinned();
    }

    private static DockItem MakePinned(string path)
        => new(Id: path,
               Label: Path.GetFileNameWithoutExtension(path),
               Icon: IconExtractor.GetIcon(path),
               Kind: DockItemKind.Pinned,
               LaunchPath: path);

    private static DockItem CreateRecycleBinItem()
        => new(Id: "shell:RecycleBinFolder",
               Label: "回收站",
               Icon: IconExtractor.GetIcon("::{645FF040-5081-101B-9F08-00AA002F954E}"), // 可能为 null → 视图回退 ♻
               Kind: DockItemKind.RecycleBin,
               LaunchPath: "shell:RecycleBinFolder");

    private static DockItem CreateMediaItem()
        => new(Id: "media", Label: "媒体", Icon: null, Kind: DockItemKind.Media, LaunchPath: string.Empty);

    // ---------- 运行窗口（实时增删 + 标题刷新） ----------

    private int SuffixCount => 2; // 回收站 + 媒体胶囊

    private static DockItem ToDockItem(WindowItem w)
        => new(Id: w.Handle.ToString(),
               Label: w.Title,
               Icon: w.Icon,
               Kind: DockItemKind.Window,
               LaunchPath: w.ExePath,
               WindowHandle: w.Handle);

    private void InsertWindowItem(int index, WindowItem w)
    {
        w.PropertyChanged += OnWindowItemPropertyChanged;
        Items.Insert(index, ToDockItem(w));
    }

    private void OnWindowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems is null) break;
                for (int i = 0; i < e.NewItems.Count; i++)
                    InsertWindowItem(_pinnedCount + e.NewStartingIndex + i, (WindowItem)e.NewItems[i]!);
                break;

            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems is null) break;
                for (int i = e.OldItems.Count - 1; i >= 0; i--)
                {
                    var w = (WindowItem)e.OldItems[i]!;
                    w.PropertyChanged -= OnWindowItemPropertyChanged;
                    Items.RemoveAt(_pinnedCount + e.OldStartingIndex + i);
                }
                break;

            case NotifyCollectionChangedAction.Replace:
                if (e.OldItems is null || e.NewItems is null) break;
                var old = (WindowItem)e.OldItems[0]!;
                old.PropertyChanged -= OnWindowItemPropertyChanged;
                var replacement = (WindowItem)e.NewItems[0]!;
                replacement.PropertyChanged += OnWindowItemPropertyChanged;
                Items[_pinnedCount + e.NewStartingIndex] = ToDockItem(replacement);
                break;

            case NotifyCollectionChangedAction.Reset:
                UnhookWindowItems();
                // 移除整个窗口块后按当前集合重挂
                while (Items.Count > SuffixCount) Items.RemoveAt(_pinnedCount);
                foreach (var w in _tracker.Windows) InsertWindowItem(Items.Count - SuffixCount, w);
                break;
        }
    }

    private void OnWindowItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WindowItem.Title) || sender is not WindowItem w) return;
        var handle = w.Handle;
        for (int i = _pinnedCount; i < Items.Count - SuffixCount; i++)
        {
            if (Items[i].WindowHandle == handle)
            {
                Items[i] = ToDockItem(w); // record 不可变 → 原位替换新实例
                return;
            }
        }
    }

    private void UnhookWindowItems()
    {
        foreach (var w in _tracker.Windows) w.PropertyChanged -= OnWindowItemPropertyChanged;
    }
}
