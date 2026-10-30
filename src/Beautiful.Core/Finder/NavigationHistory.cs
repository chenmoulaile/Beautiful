using System;
using System.Collections.Generic;

namespace Beautiful.Core.Finder;

/// <summary>
/// Finder 前进/后退导航栈（Phase 6-a 纯状态机）。
/// <para>
/// 首访只设置当前目录；重复访问当前目录是 no-op；后退后再访问新目录会清空前进栈。
/// 后退栈按构造时给定的容量裁掉最旧项（容量小于 1 时钳为 1），保证内存占用有上界。
/// 路径按 <see cref="StringComparison.Ordinal"/> 判重——是否大小写不敏感由上层文件系统的语义决定，
/// 本状态机不替它做规范化。
/// </para>
/// </summary>
public sealed class NavigationHistory
{
    private readonly int _capacity;
    private readonly List<string> _back = [];
    private readonly List<string> _forward = [];

    /// <param name="capacity">后退栈容量（默认 50），小于 1 时按 1 处理。</param>
    public NavigationHistory(int capacity = 50) => _capacity = Math.Max(1, capacity);

    /// <summary>当前目录；从未访问过时为 <c>null</c>。</summary>
    public string? Current { get; private set; }

    /// <summary>是否还有可后退的历史项。</summary>
    public bool CanGoBack => _back.Count > 0;

    /// <summary>是否还有可前进的历史项。</summary>
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>
    /// 访问一个目录：与 <see cref="Current"/> 相同则 no-op；否则把当前目录压入后退栈、
    /// 清空前进栈并更新当前目录。
    /// </summary>
    /// <param name="path">目标目录，不能为 null、空串或纯空白。</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> 为 null、空串或纯空白。</exception>
    public void Visit(string path)
    {
        // 不用 ArgumentNullException.ThrowIfNull：调用方按 ArgumentException 捕获，保持异常类型单一。
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("导航路径不能为空。", nameof(path));
        }

        if (Current is null)
        {
            Current = path;
            return;
        }

        if (string.Equals(Current, path, StringComparison.Ordinal))
        {
            return;
        }

        _back.Add(Current);
        TrimBack();
        _forward.Clear();
        Current = path;
    }

    /// <summary>
    /// 后退一步：把当前目录压入前进栈，弹出后退栈顶作为新的当前目录。
    /// </summary>
    /// <returns>新的当前目录；无法后退时返回 <c>null</c> 且不改变任何状态。</returns>
    public string? Back()
    {
        if (_back.Count == 0)
        {
            return null;
        }

        _forward.Add(Current!);
        Current = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        return Current;
    }

    /// <summary>
    /// 前进一步：把当前目录压回后退栈，弹出前进栈顶作为新的当前目录。
    /// </summary>
    /// <returns>新的当前目录；无法前进时返回 <c>null</c> 且不改变任何状态。</returns>
    public string? Forward()
    {
        if (_forward.Count == 0)
        {
            return null;
        }

        _back.Add(Current!);
        TrimBack();
        Current = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        return Current;
    }

    /// <summary>清空当前目录与前进/后退栈。</summary>
    public void Clear()
    {
        _back.Clear();
        _forward.Clear();
        Current = null;
    }

    private void TrimBack()
    {
        if (_back.Count > _capacity)
        {
            _back.RemoveRange(0, _back.Count - _capacity);
        }
    }
}
