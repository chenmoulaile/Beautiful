using System.IO;
using System.Windows;
using Beautiful.App.Services;
using Beautiful.Core.Finder;

namespace Beautiful.App.Finder;

/// <summary>
/// 访达窗口 · 搜索（Phase 6-c）：当前文件夹实时过滤 + "这台 Mac"受控递归搜索。
/// <para>
/// 两段式设计：搜索框输入时只做**内存过滤**（零 IO，逐键即时）；按 Enter 才启动递归搜索。
/// 递归由 <see cref="FinderSearchLimits"/> 三重设限（深度/条数/超时），并且
/// **不跟随重解析点**（junction/symlink 会造成目录环），因此不会出现"搜索卡死"这种经典事故。
/// </para>
/// <para>
/// 结果直接喂给当前视图（列表/图标/分栏都能显示搜索结果），Esc 退出搜索回到目录内容。
/// 这是"受控递归枚举"，不是 Everything 式全局索引——README 里如实写明边界。
/// </para>
/// </summary>
public partial class FinderWindow
{
    private readonly List<FinderRow> _searchResults = [];
    private CancellationTokenSource? _searchCts;
    private bool _searchActive;
    private bool _searching;
    private string _searchQuery = string.Empty;

    /// <summary>搜索是否处于激活态（结果集取代目录内容）。</summary>
    private bool SearchActive => _searchActive;

    /// <summary>诊断入口（<c>--search &lt;query&gt;</c>）：把查询写进搜索框并跑一次递归搜索。</summary>
    public static void RunSearchForDiagnostics(string? query)
    {
        if (_current is not { IsLoaded: true } window || string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        window.SearchBox.Text = query;
        window.StartDeepSearch();
    }

    /// <summary>工具栏"这台 Mac"按钮：与在搜索框里按 Enter 等价。</summary>
    private void Scope_Click(object sender, RoutedEventArgs e) => StartDeepSearch();

    /// <summary>Enter：对当前查询跑一次"这台 Mac"递归搜索。</summary>
    private void StartDeepSearch()
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            ShowStatusHint("先输入要搜索的名称");
            return;
        }

        CancelSearch();
        _searchActive = true;
        _searching = true;
        _searchQuery = query;
        _searchResults.Clear();
        ApplyFilter();
        UpdateStatus();

        var cts = new CancellationTokenSource(FinderSearchLimits.Default.Timeout);
        _searchCts = cts;
        var limits = FinderSearchLimits.Default;
        var root = _currentPath;

        _ = Task.Run(() => Walk(root, query, limits, cts.Token), cts.Token)
            .ContinueWith(task =>
            {
                if (cts.IsCancellationRequested && !task.IsCompletedSuccessfully)
                {
                    return;   // 被新搜索/退出取代：丢弃
                }

                if (task.IsFaulted)
                {
                    Logger.Warn($"访达搜索失败：{task.Exception?.GetBaseException().Message}");
                }

                var rows = task.IsCompletedSuccessfully ? task.Result : [];
                if (!ReferenceEquals(_searchCts, cts))
                {
                    return;
                }

                _searching = false;
                _searchResults.Clear();
                _searchResults.AddRange(rows);
                ApplyFilter();
                UpdateStatus();
                Logger.Info($"访达搜索完成：root={root} query={query} 命中={rows.Count}");
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// 递归枚举（后台线程）：广度优先、三重设限、不跟随重解析点。
    /// 每个目录单独 try/catch——无权限的目录跳过即可，不能因为一个 System Volume Information 中断整次搜索。
    /// </summary>
    private static List<FinderRow> Walk(string root, string query, FinderSearchLimits limits, CancellationToken token)
    {
        var results = new List<FinderRow>();
        var pending = new Queue<(string Directory, int Depth)>();
        pending.Enqueue((root, 0));
        var budget = FinderSearchLimits.Normalize(limits);

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            if (results.Count >= budget.MaxResults)
            {
                break;
            }

            var (directory, depth) = pending.Dequeue();
            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(directory).GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or System.Security.SecurityException or ArgumentException
                                          or NotSupportedException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var info in entries)
            {
                token.ThrowIfCancellationRequested();
                if (results.Count >= budget.MaxResults)
                {
                    break;
                }

                bool isDirectory;
                long size = 0;
                DateTime modified;
                FileAttributes attributes;
                try
                {
                    isDirectory = info is DirectoryInfo;
                    if (info is FileInfo file)
                    {
                        size = file.Length;
                    }

                    modified = info.LastWriteTimeUtc;
                    attributes = info.Attributes;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var hidden = (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
                if (FinderSearchMatcher.IsMatch(info.Name, query))
                {
                    var kind = isDirectory ? "文件夹" : Path.GetExtension(info.Name).TrimStart('.').ToLowerInvariant();
                    results.Add(new FinderRow(directory,
                        new FinderEntry(info.Name, isDirectory, size, modified,
                            string.IsNullOrEmpty(kind) ? "文件" : kind, hidden)));
                }

                // 递归：跳过重解析点（目录环）与系统卷目录
                if (isDirectory && depth < budget.MaxDepth
                    && (attributes & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Enqueue((FinderInfo.FullNameOf(directory, info.Name), depth + 1));
                }
            }
        }

        return results;
    }

    /// <summary>退出搜索：清空结果、恢复目录内容（Esc / 清空搜索框 / 导航时调用）。</summary>
    private void ExitSearch()
    {
        if (!_searchActive && _searchResults.Count == 0)
        {
            return;
        }

        CancelSearch();
        _searchActive = false;
        _searching = false;
        _searchQuery = string.Empty;
        _searchResults.Clear();
        ApplyFilter();
        UpdateStatus();
    }

    private void CancelSearch()
    {
        var cts = _searchCts;
        _searchCts = null;
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    /// <summary>搜索状态文案（供 <see cref="UpdateStatus"/> 使用）。</summary>
    private string? SearchStatusText()
    {
        if (!_searchActive)
        {
            return null;
        }

        if (_searching)
        {
            return $"正在搜索「{_searchQuery}」…";
        }

        return _searchResults.Count == 0
            ? $"在「这台 Mac」中未找到「{_searchQuery}」"
            : $"在「这台 Mac」中找到 {_searchResults.Count} 个「{_searchQuery}」结果";
    }
}

/// <summary>路径拼接的薄封装（避免 Core 的 <see cref="FinderPaths"/> 与 IO 混在一处时读错）。</summary>
internal static class FinderInfo
{
    public static string FullNameOf(string directory, string name)
    {
        try
        {
            return FinderPaths.Combine(directory, name);
        }
        catch (ArgumentException)
        {
            return directory;
        }
    }
}
