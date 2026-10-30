using System.IO;
using Beautiful.App.Interop;
using Beautiful.App.Services;

namespace Beautiful.App.Finder;

/// <summary>
/// 文件操作自检（诊断入口 <c>--fileop-smoke &lt;dir&gt;</c>）。
/// <para>
/// <c>IFileOperation</c> 的互操作是"vtable 错一个槽位就 AccessViolation"的高风险代码，光靠编译通过
/// 说明不了任何事。这个自检在真实目录上走一遍完整链路：建文件 → 拷贝 → 重命名 → 移到废纸篓，
/// 每步把 <see cref="FileOperationResult"/> 写进日志。**它只动自己创建的文件**（名字带
/// <c>beautiful-smoke-</c> 前缀），跑完把残留清掉，绝不动用户已有文件。
/// </para>
/// <para>
/// 不是产品功能：只在命令行显式要求时执行一次，结果进日志后立即退出。
/// </para>
/// </summary>
internal static class FileOperationSmoke
{
    private const string Prefix = "beautiful-smoke-";

    /// <summary>在 <paramref name="directory"/> 里跑一遍文件操作链路；返回是否全部成功。</summary>
    public static async Task<bool> RunAsync(string directory)
    {
        var service = new FileOperationService();
        var created = new List<string>();
        var allOk = true;

        try
        {
            if (!Directory.Exists(directory))
            {
                Logger.Warn($"文件操作自检：目录不存在 {directory}");
                return false;
            }

            var stamp = DateTime.Now.ToString("HHmmssfff");
            var source = Path.Combine(directory, $"{Prefix}{stamp}.txt");
            await File.WriteAllTextAsync(source, "beautiful file operation smoke\n");
            created.Add(source);
            Logger.Info($"文件操作自检：已建源文件 {source}");

            var copyTarget = Path.Combine(directory, $"{Prefix}{stamp}-copy.txt");
            var copy = await service.CopyAsync([new FileOpItem(source, copyTarget)]);
            allOk &= Report("拷贝", copy);
            if (copy.Success && File.Exists(copyTarget))
            {
                created.Add(copyTarget);
            }

            var renamedName = $"{Prefix}{stamp}-renamed.txt";
            var rename = await service.RenameAsync(copyTarget, renamedName);
            allOk &= Report("重命名", rename);
            var renamedPath = Path.Combine(directory, renamedName);
            if (rename.Success && File.Exists(renamedPath))
            {
                created.Add(renamedPath);
            }

            var recycle = await service.RecycleAsync([source]);
            allOk &= Report("移到废纸篓", recycle);
            if (recycle.Success)
            {
                created.Remove(source);
            }

            // 收尾：把还在的残留永久删掉（只删自己创建的名字前缀，双保险）
            foreach (var path in created.Where(p => Path.GetFileName(p).StartsWith(Prefix, StringComparison.Ordinal)))
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.Warn($"文件操作自检清理失败：{path} {ex.Message}");
                }
            }

            Logger.Info($"文件操作自检：{(allOk ? "全部通过" : "存在失败项")}");
            return allOk;
        }
        catch (Exception ex)
        {
            Logger.Error("文件操作自检异常", ex);
            return false;
        }
    }

    private static bool Report(string step, FileOperationResult result)
    {
        if (result.Success)
        {
            Logger.Info($"文件操作自检 · {step}：成功（{result.Completed} 项）");
            return true;
        }

        Logger.Warn($"文件操作自检 · {step}：失败 cancelled={result.Cancelled} " +
                    $"completed={result.Completed} failed={result.Failed} error={result.Error}");
        return false;
    }
}
