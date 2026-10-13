using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using Beautiful.App.Interop;
using Beautiful.App.Services;

namespace Beautiful.App.Dock;

/// <summary>
/// Dock 数据源：合并 固定应用（本任务硬编码默认项，Task 16 由 config/dock.json 接管）
/// + WindowTracker 运行窗口（实时增删）+ 回收站 + 媒体胶囊占位（Task 15 实装）。
/// 布局顺序固定为：Pinned… | Window… | 回收站 | 媒体；窗口块始终连续，映射下标 = 固定项数 + 跟踪集合下标。
/// 所有事件均在 UI 线程到达（WindowTracker 保证），无需加锁。
/// </summary>
public sealed class DockViewModel
{
    private readonly WindowTracker _tracker;
    private readonly int _pinnedCount;

    public ObservableCollection<DockItem> Items { get; } = new();

    public DockViewModel(WindowTracker tracker)
    {
        _tracker = tracker;

        foreach (var pinned in CreateDefaultPinnedItems()) Items.Add(pinned);
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

    private static IEnumerable<DockItem> CreateDefaultPinnedItems()
    {
        // 本任务的硬编码默认固定项；Label 暂用 exe 名（Task 16 接管为配置）
        yield return MakePinned(@"C:\Windows\explorer.exe");
        yield return MakePinned(@"C:\Windows\System32\notepad.exe");
        var edge = MakePinned(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe");
        if (File.Exists(edge.LaunchPath)) yield return edge;
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
