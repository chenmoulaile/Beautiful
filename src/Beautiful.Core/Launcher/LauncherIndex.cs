namespace Beautiful.Core.Launcher;

/// <summary>
/// 启动器的一条可启动项（v3 T7）。三件事：叫什么、怎么启动、图标从哪儿取。
/// Core 只认这三样数据，<b>不解析 .lnk、不读注册表、不碰 shell</b>——那些是 App 侧扫描器
/// （<c>LauncherIndexSource</c>）的活，它把扫到的东西折算成本记录后交给 <see cref="LauncherIndex"/>。
/// </summary>
/// <param name="Name">界面显示名（扫描器已剥掉 ".lnk"；这里不再二次加工，见 <see cref="LauncherIndex.Build"/>）。</param>
/// <param name="LaunchPath">真正交给 shell 启动的目标：快捷方式解析出的 exe，或解析失败时的 .lnk 本身。</param>
/// <param name="Arguments">快捷方式自带的参数（启动时原样带上，否则"带参数启动"的那批快捷方式会启动错）。</param>
/// <param name="IconPath">图标源：<b>优先给 .lnk 本身</b>，因为 App 侧的图标降级链第 ③ 层正是
/// "解析 .lnk 后在目标/IconLocation 上重跑"——把 .lnk 交进去能让图标跟着快捷方式自定义图标走。</param>
public sealed record LauncherApp(
    string Name,
    string LaunchPath,
    string Arguments = "",
    string? IconPath = null);

/// <summary>一条命中结果：应用 + 分值（分值进 UI 无意义，但它是排序与单测的可观测面）。</summary>
public sealed record LauncherMatch(LauncherApp App, int Score);

/// <summary>
/// 启动器索引：把"扫描器给的一堆应用"整理成<b>确定有序</b>的列表，并按查询打分取前 N 条。
/// <para>
/// 【为什么这一层必须在 Core】"输入同样的查询必须得到同样的次序"是启动器的可测契约：同名去重按什么键、
/// 空查询显示什么、打分档次与并列时的先后——这些一旦散在 WPF 里就再也钉不住。这里是一个只读结构 +
/// 两个纯函数（<see cref="Build"/>、<see cref="Search"/>），单测直接构造输入即可复现。
/// </para>
/// <para>
/// 【确定性怎么保证】① Build 先按 (名称, 路径) 稳定排序再入表；② 去重键与排序比较器都是
/// Ordinal/OrdinalIgnoreCase 系列，不受当前区域设置影响（中文系统上"按拼音排"会随 ICU 版本变）；
/// ③ 打分只依赖名称、目标两个字段与查询（目标字段的推导见 <see cref="LauncherTarget"/>），并列时依次比"名称长度 → 名称 Ordinal → 路径 Ordinal"，全序无环。
/// </para>
/// </summary>
public sealed class LauncherIndex
{
    // ---------- 打分档（写成常量是为了让单测断言"为什么这条排前面"，而不是只断言次序） ----------

    /// <summary>名称与查询完全相等。</summary>
    public const int ScoreExact = 1000;

    /// <summary>名称以查询开头（"calc" → Calculator）。</summary>
    public const int ScorePrefix = 700;

    /// <summary>首字母序列以查询开头（"jsq" → 计算器、"tm" → Task Manager）。</summary>
    public const int ScoreInitialsPrefix = 660;

    /// <summary>某个词（空格/连字符/括号切开）以查询开头（"mate" → Pro Mate）。比整名前缀低一档。</summary>
    public const int ScoreWordPrefix = 600;

    /// <summary>名称里出现查询子串（"editor" → Visual Studio Code - text editor）。</summary>
    public const int ScoreContains = 350;

    /// <summary>首字母序列里出现查询子串（比名称子串低一档：短查询时它命中得太宽）。</summary>
    public const int ScoreInitialsContains = 300;

    // ---------- 目标字段档（v3 T7c）----------
    //
    // 【为什么要整套目标档】用户敲的是 exe 名（charmap / cmd / notepad / calc），显示名里往往一个字都没有。
    // 【为什么整体压在同名次档的下一档】同一串输入既像名字又像目标时，"名字就是它"更值得排前面：
    // 查 "cmd" 时名为 "cmd" 的条目应该压过名为 "命令提示符" 而目标为 cmd.exe 的条目（同一应用时用户看到的
    // 是两个入口，名字相同的那个才是"直接那个"）；查 "code" 时名为 "Code - text editor" 的整名前缀
    // 也该在目标前缀之前。次序一览（高→低）：
    // 名称完全 > 目标完全 > 名称前缀 > 目标前缀 > 名称首字母前缀 > 目标首字母前缀
    // > 名称词首 > 目标词首 > 名称子串 > 目标子串 > 名称首字母子串 > 目标首字母子串。
    // 例外只有一处：**目标完全相等（900）压过名称前缀（700）**——查 "charmap" 时
    // "Character Map/charmap.exe" 必须排第一，而它名字里根本没有 "charmap"。

    /// <summary>目标文件名（不含扩展）与查询完全相等（"charmap" → charmap.exe）。仅次于名称完全相等。</summary>
    public const int ScoreTargetExact = 900;

    /// <summary>目标文件名以查询开头。</summary>
    public const int ScoreTargetPrefix = 680;

    /// <summary>目标文件名的首字母序列以查询开头。</summary>
    public const int ScoreTargetInitialsPrefix = 640;

    /// <summary>目标文件名的某个词以查询开头。</summary>
    public const int ScoreTargetWordPrefix = 570;

    /// <summary>目标文件名里出现查询子串（"calc" → windows<b>calc</cator> 这类中间命中）。</summary>
    public const int ScoreTargetContains = 340;

    /// <summary>目标文件名的首字母序列里出现查询子串。</summary>
    public const int ScoreTargetInitialsContains = 280;

    private readonly Entry[] _entries;

    private LauncherIndex(Entry[] entries) => _entries = entries;

    /// <summary>索引内条目数（诊断/验收用）。</summary>
    public int Count => _entries.Length;

    /// <summary>空索引（扫描失败/没权限时的合法结果：界面据此显示"未找到应用"，绝不显示假数据）。</summary>
    public static LauncherIndex Empty { get; } = new([]);

    /// <summary>全部条目（Build 后的确定次序，按名称排）。空查询时界面显示的就是这一段。</summary>
    public IReadOnlyList<LauncherApp> All
        => _entries.Length == 0 ? [] : Array.AsReadOnly(_entries.Select(e => e.App).ToArray());

    /// <summary>一个可搜索字段的六个档（名称与目标共用同一套判据，只差在档次数值上）。</summary>
    private readonly record struct Tier(int Exact, int Prefix, int InitialsPrefix, int WordPrefix, int Contains, int InitialsContains);

    private static readonly Tier NameTier = new(
        ScoreExact, ScorePrefix, ScoreInitialsPrefix, ScoreWordPrefix, ScoreContains, ScoreInitialsContains);

    private static readonly Tier TargetTier = new(
        ScoreTargetExact, ScoreTargetPrefix, ScoreTargetInitialsPrefix, ScoreTargetWordPrefix,
        ScoreTargetContains, ScoreTargetInitialsContains);

    /// <summary>
    /// 一条索引项：应用 + 名称检索键 + 名称首字母序列 + 目标检索键 + 目标首字母序列。
    /// 目标键在 <see cref="Build"/> 里一次算好——检索是每次敲字都要跑的，把 <see cref="LauncherTarget"/> 的
    /// 字符串加工放进热路径就是白费一次路径解析。
    /// </summary>
    private readonly record struct Entry(
        LauncherApp App, string Key, IReadOnlyList<string> Initials,
        string TargetKey, IReadOnlyList<string> TargetInitials);

    /// <summary>
    /// 规范化 + 去重 + 定序。<para/>
    /// 名称空或路径空的条目直接丢（"没有名字的应用"无法展示也无法启动，留着只会在界面上画成空行）；
    /// 名称末尾的 ".lnk" 剥掉（Windows 的快捷方式名常带扩展名，用户眼里那是"计算器"不是"计算器.lnk"）；
    /// 去重键见 <see cref="LauncherTarget.IdentityOf"/>：<b>AppsFolder 的 AUMID</b>（同一条 UWP 应用，
    /// 开始菜单的 .lnk 与 MSIX 枚举各给一条 → 只留一条）或<b>启动目标 + 参数</b>（OrdinalIgnoreCase）：
    /// 同一个 exe 被开始菜单在"用户/机器"两处、或"根目录 + 子文件夹"两处放了两条，只留第一条——
    /// 参数也进键，因此"edge.exe --app=x"这类同名不同目的的快捷方式不会被误合成一条。
    /// </summary>
    public static LauncherIndex Build(IEnumerable<LauncherApp> apps)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<Entry>();
        foreach (var app in apps)
        {
            var name = (app.Name ?? string.Empty).Trim();
            if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                name = name[..^4].Trim();
            if (name.Length == 0 || string.IsNullOrWhiteSpace(app.LaunchPath)) continue;

            var path = app.LaunchPath.Trim();
            if (!seen.Add(LauncherTarget.IdentityOf(path, app.Arguments))) continue;

            var targetKey = LauncherTarget.SearchKeyOf(path, app.Arguments);
            kept.Add(new Entry(app with { Name = name, LaunchPath = path },
                               name.ToLowerInvariant(),
                               PinyinInitials.Positions(name),
                               targetKey,
                               PinyinInitials.Positions(targetKey)));
        }

        // 入表定序：名称（忽略大小写）→ 名称 Ordinal → 路径 Ordinal。这是"空查询"与并列同分时的显示序，
        // 也是 Search 结果的稳定基底（排序只在 Search 里按分值再排一次，见 OrderBy 链）。
        kept.Sort((a, b) =>
        {
            var byName = string.Compare(a.App.Name, b.App.Name, StringComparison.OrdinalIgnoreCase);
            if (byName != 0) return byName;
            byName = string.CompareOrdinal(a.App.Name, b.App.Name);
            if (byName != 0) return byName;
            return string.CompareOrdinal(a.App.LaunchPath, b.App.LaunchPath);
        });
        return new LauncherIndex(kept.ToArray());
    }

    /// <summary>
    /// 检索：<b>空查询返回索引前 <paramref name="limit"/> 条</b>（按 Build 的名称序）。
    /// 这是有意的取舍：Spotlight 在无查询时给"最近使用/建议"，而本索引没有任何使用频度数据，
    /// 造一份"看起来像建议"的列表就是假数据——于是给真实的、确定有序的前若干条。
    /// </summary>
    public IReadOnlyList<LauncherMatch> Search(string? query, int limit)
    {
        if (_entries.Length == 0 || limit <= 0) return [];
        if (string.IsNullOrWhiteSpace(query))
            return _entries.Take(limit).Select(e => new LauncherMatch(e.App, 0)).ToArray();

        var trimmed = query.Trim();
        var hits = new List<LauncherMatch>();
        foreach (var entry in _entries)
        {
            var score = Score(trimmed, entry);
            if (score > 0) hits.Add(new LauncherMatch(entry.App, score));
        }

        // 分值降序 → 名称更短优先（同样命中 "cmd" 时 "命令提示符" 不该被 "命令提示符（管理员）" 压住）
        // → 名称 Ordinal → 路径 Ordinal：四个比较维度全为 Ordinal，跨机器/跨区域同序。
        hits.Sort((a, b) =>
        {
            if (b.Score != a.Score) return b.Score.CompareTo(a.Score);
            var byLength = a.App.Name.Length.CompareTo(b.App.Name.Length);
            if (byLength != 0) return byLength;
            byLength = string.CompareOrdinal(a.App.Name, b.App.Name);
            if (byLength != 0) return byLength;
            return string.CompareOrdinal(a.App.LaunchPath, b.App.LaunchPath);
        });
        return hits.Take(limit).ToArray();
    }

    /// <summary>
    /// 单条打分（0 = 不匹配）。档次的含义见 <c>Score*</c> 常量：文本命中与首字母命中各三档，
    /// <b>名称与目标两个字段各打一次，取最高档</b>（一个词同时是整名前缀与目标名时，取更说明"就是它"的那一档）。
    /// </summary>
    public static int Score(string query, LauncherApp app)
        => Score(query, new Entry(app,
                                  (app.Name ?? string.Empty).ToLowerInvariant(),
                                  PinyinInitials.Positions(app.Name),
                                  LauncherTarget.SearchKeyOf(app.LaunchPath, app.Arguments),
                                  PinyinInitials.Positions(
                                      LauncherTarget.SearchKeyOf(app.LaunchPath, app.Arguments))));

    private static int Score(string query, Entry entry)
        => Math.Max(
            ScoreField(query, entry.Key, entry.Initials, NameTier),
            ScoreField(query, entry.TargetKey, entry.TargetInitials, TargetTier));

    /// <summary>
    /// 单个字段打分：<b>判据完全对称</b>（六个档按 Exact → Prefix → 首字母前缀 → 词首 → 子串 → 首字母子串 依次降级），
    /// 名称与目标只是给的分数不同（见 <c>ScoreTarget*</c> 常量的注释）。取最高档而非求和：
    /// 求和会让"目标里恰好含有查询"的长串堆出高分，把一个不相干的应用顶到前面。
    /// </summary>
    private static int ScoreField(string query, string key, IReadOnlyList<string> initials, Tier tier)
    {
        // 先 Trim 再比：检索键里可以合法地含空格（"command prompt"），不剥掉空白查询就会以"子串"的
        // 名义命中一堆条目（Search 自己会拦纯空白，但 Score 是公开的，任何调用方都可能传进来）。
        var q = query.Trim().ToLowerInvariant();
        if (q.Length == 0 || key.Length == 0) return 0;

        var score = 0;
        if (key == q) score = tier.Exact;
        else if (key.StartsWith(q, StringComparison.Ordinal)) score = tier.Prefix;
        else if (StartsWithWord(key, q)) score = tier.WordPrefix;
        else if (key.Contains(q, StringComparison.Ordinal)) score = tier.Contains;

        if (score < tier.InitialsPrefix)
        {
            if (PinyinInitials.Matches(initials, q, prefixOnly: true))
                score = Math.Max(score, tier.InitialsPrefix);
            else if (PinyinInitials.Matches(initials, q, prefixOnly: false))
                score = Math.Max(score, tier.InitialsContains);
        }
        return score;
    }

    /// <summary>按分隔符切词后，是否有某一词以查询开头（"code" → "vs code" 的第二个词）。</summary>
    private static bool StartsWithWord(string key, string query)
    {
        var start = 0;
        while (start <= key.Length)
        {
            var end = key.Length;
            for (var i = start; i < key.Length; i++)
                if (IsWordBreak(key[i])) { end = i; break; }
            var span = key.AsSpan(start, end - start);
            if (span.Length >= query.Length && span.StartsWith(query, StringComparison.Ordinal)) return true;
            if (end == key.Length) break;
            start = end + 1;
        }
        return false;
    }

    private static bool IsWordBreak(char c) =>
        c is ' ' or '-' or '_' or '(' or ')' or '[' or ']' or '/' or '\\';
}
