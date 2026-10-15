using System.Text.Json;

namespace Beautiful.Core.Dock;

/// <summary>
/// Dock 固定列表（dock.json）的**纯编辑算术**：插入/移动/移除的下标换算、按"解析后的目标"去重、
/// 以及结构损坏 JSON 的自愈解析。放在 Core 是为了让这些规则可单测——拖放固定/排序最容易出错的就是
/// "移除前插入槽"与"移除后索引"的错位，以及"同一个 exe 既是 .lnk 又是本体"的双重固定，
/// 这些都不该散落在 App 的事件处理器里靠肉眼保证。
/// <para>
/// 目标解析（.lnk → 真实 exe）由调用方以 <paramref name="targetOf"/> 委托注入：Core 不引 ShellLink（COM），
/// 单测则注入恒等或假映射即可覆盖去重规则。传 null = 按原始路径（忽略大小写）去重，与历史 Load 行为一致。
/// </para>
/// </summary>
public static class DockPinOps
{
    /// <summary>
    /// 结构自愈解析：把 dock.json 文本读成原始路径串。语法损坏 / 非数组 / "null" 字面量 → false
    /// （调用方回落代码默认，与 <c>AppSettingsStore</c> 的自愈网同一 idiom）。值域问题（空行、重复）
    /// 不是结构损坏，交给 <see cref="Normalize"/> 收敛并保留其余项。
    /// </summary>
    public static bool TryParsePaths(string json, out List<string> raw)
    {
        raw = new List<string>();
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(json);
            if (list is null) return false;   // "null" 字面量：STJ 反序列化成 null，等同损坏
            raw = list;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 归一：丢弃空白项 + 按目标（<paramref name="targetOf"/> 或原串，忽略大小写）去重，
    /// 保留每个目标**首次出现的原始串**与相对顺序。空/坏路径不影响其余项（与 Load 现状一致，只更严）。
    /// </summary>
    public static List<string> Normalize(IEnumerable<string> raw, Func<string, string>? targetOf = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var path in raw)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var key = KeyOf(path, targetOf);
            if (key.Length == 0) { result.Add(path); continue; }   // 解析不出目标：原样保留，不静默丢
            if (seen.Add(key)) result.Add(path);
        }
        return result;
    }

    /// <summary>目标在列表中的下标；无则 -1。比较用忽略大小写的目标键。</summary>
    public static int IndexOfTarget(IReadOnlyList<string> list, string target, Func<string, string>? targetOf = null)
    {
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(KeyOf(list[i], targetOf), target, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// 在 <paramref name="slot"/>（**移除前**坐标系里的插入槽，钳到 <c>[0, count]</c>）插入 <paramref name="path"/>。
    /// 若其目标已在表中，则不新增、<paramref name="added"/>=false、返回既有项下标（调用方据此决定是否需要 Move 到位）。
    /// 返回插入后该项的下标。
    /// </summary>
    public static int Insert(List<string> list, string path, int slot, Func<string, string>? targetOf, out bool added)
    {
        var existing = IndexOfTarget(list, KeyOf(path, targetOf), targetOf);
        if (existing >= 0)
        {
            added = false;
            return existing;
        }
        var at = Math.Clamp(slot, 0, list.Count);
        list.Insert(at, path);
        added = true;
        return at;
    }

    /// <summary>
    /// 把 <paramref name="from"/> 处的项移动到 <paramref name="slot"/>（**移除前**坐标系的插入槽，钳到 <c>[0, count]</c>）。
    /// 经典陷阱：先移除会让后面的槽位左移一格，故 <c>from &lt; slot</c> 时目标槽减一。from 越界返回 false（不改表）。
    /// </summary>
    public static bool MoveToSlot(List<string> list, int from, int slot)
    {
        if (from < 0 || from >= list.Count) return false;
        // 槽位先在**移除前**坐标系里钳到 [0, count]（拖放指示条用的就是这个坐标系的槽），
        // 再移除、再按"from 在槽左侧则槽左移一格"换算到移除后坐标——顺序反了会把"移到末尾"钳成"移到倒数第二"。
        var target = Math.Clamp(slot, 0, list.Count);
        var item = list[from];
        list.RemoveAt(from);
        if (from < target) target--;
        list.Insert(target, item);
        return true;
    }

    /// <summary>按目标移除；找到并移除返回 true，否则 false。</summary>
    public static bool Remove(List<string> list, string path, Func<string, string>? targetOf = null)
    {
        var idx = IndexOfTarget(list, KeyOf(path, targetOf), targetOf);
        if (idx < 0) return false;
        list.RemoveAt(idx);
        return true;
    }

    private static string KeyOf(string path, Func<string, string>? targetOf)
    {
        if (targetOf is null) return path.Trim();
        try { return targetOf(path)?.Trim() ?? path.Trim(); }
        catch { return path.Trim(); }   // 解析器（ShellLink/COM）对坏快捷方式可能抛：退回原串，绝不丢项
    }
}
