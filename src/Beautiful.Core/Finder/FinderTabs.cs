using System;
using System.Collections.Generic;

namespace Beautiful.Core.Finder;

/// <summary>
/// 访达标签页集合（Phase 6-c 纯状态机）。
/// <para>
/// 每个标签**各自持有一条 <see cref="NavigationHistory"/>**：这是 macOS 标签页的关键语义——
/// 在标签 A 里后退三次，切到标签 B 再切回来，A 的前进/后退栈必须原样还在。
/// 因此本类不做"共享历史 + 记录位置"的省事实现，宁可每个标签一个栈对象。
/// </para>
/// <para>
/// 规则：新建标签一律插在当前标签右侧并激活（Safari/Finder 的行为）；关闭标签后激活右邻居，
/// 没有右邻居则激活左邻居；最后一个标签不可关闭（关窗是窗口层的决定，不由标签集合表达）。
/// 标签数上限 <see cref="MaxTabs"/>，超出时拒绝新建（返回 -1），由调用方给出提示。
/// </para>
/// </summary>
public sealed class FinderTabSet
{
    /// <summary>标签数上限（macOS 允许很多，但一个窗口 12 个已经远超日常使用）。</summary>
    public const int MaxTabs = 12;

    private readonly List<Tab> _tabs = [];

    /// <param name="initialPath">首个标签的路径，不能为 null/空白。</param>
    /// <param name="maxTabs">标签上限，小于 1 时按 1 处理。</param>
    /// <exception cref="ArgumentException"><paramref name="initialPath"/> 为空。</exception>
    public FinderTabSet(string initialPath, int maxTabs = MaxTabs)
    {
        if (string.IsNullOrWhiteSpace(initialPath))
        {
            throw new ArgumentException("标签路径不能为空。", nameof(initialPath));
        }

        MaxTabsValue = Math.Max(1, maxTabs);
        _tabs.Add(new Tab(initialPath));
        ActiveIndex = 0;
    }

    /// <summary>标签上限（构造时钳过）。</summary>
    public int MaxTabsValue { get; }

    /// <summary>标签数量（恒 ≥ 1）。</summary>
    public int Count => _tabs.Count;

    /// <summary>当前激活的标签索引。</summary>
    public int ActiveIndex { get; private set; }

    /// <summary>当前标签的路径。</summary>
    public string ActivePath => _tabs[ActiveIndex].Path;

    /// <summary>当前标签的导航历史（窗口后退/前进直接用它）。</summary>
    public NavigationHistory ActiveHistory => _tabs[ActiveIndex].History;

    /// <summary>标签路径快照（顺序即显示顺序）。</summary>
    public IReadOnlyList<string> Paths
    {
        get
        {
            var result = new string[_tabs.Count];
            for (var i = 0; i < _tabs.Count; i++)
            {
                result[i] = _tabs[i].Path;
            }

            return result;
        }
    }

    /// <summary>是否还有可关闭的标签（只剩一个时为 false）。</summary>
    public bool CanClose => _tabs.Count > 1;

    /// <summary>在某标签上导航（更新该标签的路径与历史）。</summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> 为空。</exception>
    public void Visit(string path)
    {
        _tabs[ActiveIndex].History.Visit(path);
        _tabs[ActiveIndex].Path = path;
    }

    /// <summary>
    /// 当前标签后退一步（同时把标签路径同步成历史里的新当前目录）。
    /// </summary>
    /// <returns>新的当前目录；无法后退返回 <c>null</c> 且不改状态。</returns>
    public string? Back()
    {
        var path = ActiveHistory.Back();
        if (path is not null)
        {
            _tabs[ActiveIndex].Path = path;
        }

        return path;
    }

    /// <summary>当前标签前进一步（同时同步标签路径）。无法前进返回 <c>null</c>。</summary>
    public string? Forward()
    {
        var path = ActiveHistory.Forward();
        if (path is not null)
        {
            _tabs[ActiveIndex].Path = path;
        }

        return path;
    }

    /// <summary>
    /// 新建标签（插入当前标签右侧并激活）。
    /// </summary>
    /// <param name="path">新标签路径；空则返回 -1。</param>
    /// <returns>新标签索引；达到上限或路径非法返回 -1。</returns>
    public int Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || _tabs.Count >= MaxTabsValue)
        {
            return -1;
        }

        var index = ActiveIndex + 1;
        _tabs.Insert(index, new Tab(path));
        ActiveIndex = index;
        return index;
    }

    /// <summary>激活某标签；越界返回 false 且不改状态。</summary>
    public bool Activate(int index)
    {
        if (index < 0 || index >= _tabs.Count || index == ActiveIndex)
        {
            return false;
        }

        ActiveIndex = index;
        return true;
    }

    /// <summary>
    /// 关闭某标签：关闭后优先激活右邻居，否则左邻居。只剩一个标签时拒绝（返回 false）。
    /// </summary>
    public bool Close(int index)
    {
        if (_tabs.Count <= 1 || index < 0 || index >= _tabs.Count)
        {
            return false;
        }

        _tabs.RemoveAt(index);
        if (index < ActiveIndex)
        {
            ActiveIndex--;
        }
        else if (index == ActiveIndex)
        {
            ActiveIndex = Math.Min(index, _tabs.Count - 1);
        }

        ActiveIndex = Math.Clamp(ActiveIndex, 0, _tabs.Count - 1);
        return true;
    }

    /// <summary>标签显示名（取路径末段；盘符根显示 <c>C:</c>）。</summary>
    public string TitleAt(int index)
        => index >= 0 && index < _tabs.Count ? FinderPaths.DisplayName(_tabs[index].Path) : string.Empty;

    /// <summary>单个标签的内部状态：路径 + 独立历史栈。</summary>
    private sealed class Tab
    {
        public Tab(string path)
        {
            Path = path;
            History.Visit(path);
        }

        public string Path { get; set; }

        public NavigationHistory History { get; } = new();
    }
}
