using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Beautiful.Core.Notifications;

/// <summary>
/// 一条通知（Windows toast 在通知中心里的形态）。
/// <para>
/// 这是通知正文在程序里的**唯一**存放形态：不序列化、不写盘、不落日志（隐私边界，
/// 与 README「运行期不落任何通知正文」一致）。字段全部可空/可空串——Win32 程序发的 toast
/// 可能没有 AppUserModelId，也可能只有标题没有正文，UI 必须能只凭其中任意一项渲染。
/// </para>
/// </summary>
/// <param name="Id">系统通知 id（UserNotificationListener 的 UInt32，多次枚举稳定）。</param>
/// <param name="AppId">AppUserModelId（可能为空串）。点击跳转用。</param>
/// <param name="AppName">应用显示名（可能为空串）。</param>
/// <param name="Title">首行文本（可能为空串）。</param>
/// <param name="Body">其余文本行，已用换行连接（可能为空串）。</param>
/// <param name="CreatedAt">系统给出的创建时间（本地时区）。</param>
public sealed record NotificationItem(
    uint Id,
    string AppId,
    string AppName,
    string Title,
    string Body,
    DateTimeOffset CreatedAt)
{
    /// <summary>一行里显示的发件应用；永不空白（无名 toast 也要能归属）。</summary>
    public string DisplayApp =>
        NotificationText.FirstNonEmpty(AppName, AppId, "通知");

    /// <summary>卡片标题：优先标题，其次正文首行，最后退回应用名。</summary>
    public string Headline =>
        NotificationText.FirstNonEmpty(Title, NotificationText.FirstLine(Body), DisplayApp);

    /// <summary>
    /// 卡片正文：有标题时就是正文；没有标题时去掉被当作标题的首行，避免同一句显示两遍。
    /// 没有剩余内容时返回空串（UI 只显示一行）。
    /// </summary>
    public string Detail => string.IsNullOrWhiteSpace(Title)
        ? NotificationText.CleanMultiline(NotificationText.SkipFirstLine(Body), int.MaxValue)
        : NotificationText.CleanMultiline(Body, int.MaxValue);

    /// <summary>应用名的首字符（面板里当图标字标；空白已在上游收敛）。</summary>
    public string Initial => DisplayApp.Length == 0 ? "?" : DisplayApp[..1].ToUpperInvariant();

    /// <summary>相对时间：刚刚 / N 分钟前 / N 小时前 / 昨天 / M月d日。</summary>
    public string RelativeTime(DateTimeOffset now)
    {
        var elapsed = now - CreatedAt;
        if (elapsed < TimeSpan.Zero)
        {
            return "刚刚";   // 时钟漂移/未来时间：不显示"负几分钟前"这种假话
        }

        if (elapsed.TotalSeconds < 60)
        {
            return "刚刚";
        }

        if (elapsed.TotalMinutes < 60)
        {
            return $"{(int)elapsed.TotalMinutes} 分钟前";
        }

        if (elapsed.TotalHours < 24 && CreatedAt.Date == now.Date)
        {
            return $"{(int)elapsed.TotalHours} 小时前";
        }

        if (CreatedAt.Date == now.Date.AddDays(-1))
        {
            return "昨天";
        }

        return CreatedAt.ToString("M月d日", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// 通知文本的清洗与截断纯函数。
/// <para>
/// toast 文本来自任意应用：可能带换行、连续空格、超长正文（一次推送几十 KB 的日志型通知）。
/// 这里只做三件事：折叠空白、按字符上限截断（带省略号）、取首个非空。全部可单测，UI 不重复实现。
/// </para>
/// </summary>
public static class NotificationText
{
    /// <summary>标题的字符上限：一条失控的 toast 不该把一行撑成一段。</summary>
    public const int MaxTitleChars = 200;

    /// <summary>正文的字符上限。</summary>
    public const int MaxBodyChars = 600;

    /// <summary>折叠空白（含换行）后截断；null/空白返回空串。</summary>
    public static string Clean(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            builder.Append(ch);
            lastWasSpace = false;
        }

        var cleaned = builder.ToString().TrimEnd();
        if (maxChars <= 0)
        {
            return string.Empty;
        }

        return cleaned.Length <= maxChars ? cleaned : cleaned[..maxChars].TrimEnd() + "…";
    }

    /// <summary>保留换行的清洗（正文用：多行正文比挤成一行好读），同样折叠行内空白。</summary>
    public static string CleanMultiline(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(line => Clean(line, int.MaxValue))
            .Where(line => line.Length > 0)
            .ToList();

        var joined = string.Join("\n", lines);
        if (joined.Length == 0)
        {
            return string.Empty;
        }

        return maxChars <= 0 || joined.Length <= maxChars
            ? joined
            : joined[..maxChars].TrimEnd() + "…";
    }

    /// <summary>正文首行（没有则空串）。</summary>
    public static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var newline = normalized.IndexOf('\n');
        return Clean(newline < 0 ? normalized : normalized[..newline], MaxTitleChars);
    }

    /// <summary>去掉首行后的剩余文本（没有换行返回空串）。</summary>
    public static string SkipFirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var newline = normalized.IndexOf('\n');
        return newline < 0 ? string.Empty : normalized[(newline + 1)..];
    }

    /// <summary>返回第一个非空串（已 trim）；全部为空时返回空串（调用方自己给兜底文案）。</summary>
    public static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return string.Empty;
    }
}

/// <summary>
/// 通知历史（仅内存）。
/// <para>
/// 语义按 macOS 通知中心对齐，并由单测钉住：
/// ① **首次同步只做基线**——打开应用前系统里已有的历史不当作"新到达"，不触发任何提示；
/// ② 按 id 去重，同一个 id 再次出现时用新内容覆盖（toast 正文可能被后续更新）；
/// ③ 系统已不再返回的旧条目**保留**在历史里（用户没读过的通知不该因为我们轮询而消失）；
/// ④ 上限 100 条，超出丢最旧的；
/// ⑤ 线程不安全——只在 UI 线程调用（服务层负责 marshal，不在这里加锁假装线程安全）。
/// </para>
/// </summary>
public sealed class NotificationStore
{
    /// <summary>历史深度上限。</summary>
    public const int MaxItems = 100;

    private readonly List<NotificationItem> _items = [];

    /// <summary>当前历史（最新在前，只读视图）。</summary>
    public IReadOnlyList<NotificationItem> Items => _items;

    /// <summary>是否已完成首次基线（此前调用 <see cref="Apply"/> 不产生"新到达"）。</summary>
    public bool Primed { get; private set; }

    public int Count => _items.Count;

    /// <summary>
    /// 并入一次系统枚举结果，返回**新到达**的条目（首次调用返回空，只建立基线）。
    /// </summary>
    public IReadOnlyList<NotificationItem> Apply(IEnumerable<NotificationItem>? incoming)
    {
        var snapshot = (incoming ?? Enumerable.Empty<NotificationItem>())
            .Where(item => item.Id != 0)
            .ToList();

        var existing = _items.ToDictionary(item => item.Id);
        var added = new List<NotificationItem>();

        foreach (var item in snapshot)
        {
            if (existing.ContainsKey(item.Id))
            {
                existing[item.Id] = item;   // 同 id：内容以最新一次为准
                continue;
            }

            existing[item.Id] = item;
            if (Primed)
            {
                added.Add(item);
            }
        }

        _items.Clear();
        _items.AddRange(existing.Values
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Take(MaxItems));

        if (!Primed)
        {
            Primed = true;
            return Array.Empty<NotificationItem>();
        }

        // 只有仍在上限内的新条目才算"新到达"（被挤出历史的极旧通知不弹提示）
        var kept = _items.Select(item => item.Id).ToHashSet();
        return added.Where(item => kept.Contains(item.Id)).ToList();
    }

    /// <summary>移除一条（用户单条划掉；系统侧删除由服务层负责）。</summary>
    public bool Remove(uint id) => _items.RemoveAll(item => item.Id == id) > 0;

    /// <summary>清空历史（不清系统通知；系统侧由服务层 ClearNotifications）。</summary>
    public void Clear() => _items.Clear();

    /// <summary>重新开始基线（用户重新授权/服务重启时调用）。</summary>
    public void ResetBaseline()
    {
        _items.Clear();
        Primed = false;
    }
}
