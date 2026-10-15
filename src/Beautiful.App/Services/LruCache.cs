namespace Beautiful.App.Services;

/// <summary>
/// 极简 LRU 缓存（**非线程安全**，调用方都在 UI 线程：图标提取与烘焙都发生在条目构建期）。
/// <para>
/// 【为什么必须是 bounded】图标位图是本进程里最大宗的托管外内存（一项 88²×4B ≈ 31KB，
/// 极端 288²×4B ≈ 331KB）。无上限的字典在"用户来回拖图标尺寸滑杆 + 反复切主题"下会一直长，
/// 而这正是 v3 内存验收要看的场景。上限换掉的代价只是偶尔多一次重烘焙（低频路径，不在逐帧里）。
/// </para>
/// <para>
/// 【为什么不用 MemoryCache / 第三方库】要的就是"容量上限 + 最近使用淘汰 + 一键清空"三件事，
/// Dictionary + LinkedList 三十行搞定，不为此引入依赖（发布产物要能被审计）。
/// </para>
/// </summary>
internal sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map = new();
    private readonly LinkedList<Entry> _order = new();   // 首 = 最近使用

    /// <summary>条目（键 + 值一起放链表节点里，淘汰时不用再反查字典）。</summary>
    private readonly record struct Entry(TKey Key, TValue Value);

    public LruCache(int capacity) => _capacity = Math.Max(1, capacity);

    /// <summary>当前条目数（诊断/单测用）。</summary>
    public int Count => _map.Count;

    public bool TryGet(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            // 命中即前移到首：LinkedList 的 unlink/link 是 O(1)，不复制节点内容
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        if (_map.TryGetValue(key, out var old))
        {
            _order.Remove(old);
            _map.Remove(key);
        }
        var node = new LinkedListNode<Entry>(new Entry(key, value));
        _order.AddFirst(node);
        _map[key] = node;
        while (_map.Count > _capacity)
        {
            var last = _order.Last;
            if (last is null) break;
            _order.RemoveLast();
            _map.Remove(last.Value.Key);
        }
    }

    public void Clear()
    {
        _map.Clear();
        _order.Clear();
    }
}
