using System;
using System.IO;
using System.Linq;

namespace Beautiful.Core.Dock;

/// <summary>
/// 自定义 Dock 图标的规则（P7 补：配合 macosicons 一类图标站）。
/// <para>
/// 我们**不内置任何 Apple 图标**（授权不允许），只允许用户自己选一张图替换某个固定项。
/// 因此这里只做三件事：认扩展名、验文件真实存在、把路径归一成绝对路径；不读图、不缩图、不落盘。
/// 图片本身能否解码由 App 侧解码器决定——解不出来就保持原图标并说明原因，绝不显示马赛克。
/// </para>
/// </summary>
public static class DockIconOverride
{
    /// <summary>允许的扩展名（WIC 能解、且用户在图标站下载到的常见格式）。</summary>
    public static readonly string[] AllowedExtensions = [".png", ".ico", ".jpg", ".jpeg", ".bmp"];

    /// <summary>单文件大小上限（8MB）：图标站的原图常有 1024px，8MB 足够且防止误选视频。</summary>
    public const long MaxBytes = 8L * 1024 * 1024;

    /// <summary>
    /// 校验用户选的文件能否当图标。成功返回归一后的绝对路径，失败返回 null 并给出原因（给状态栏/对话框用）。
    /// </summary>
    public static string? Validate(string? path, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "没有选择文件";
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "路径无效";
            return null;
        }

        if (!File.Exists(full))
        {
            reason = "文件不存在";
            return null;
        }

        var extension = Path.GetExtension(full).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension, StringComparer.Ordinal))
        {
            reason = $"不支持的格式（{string.Join(" / ", AllowedExtensions)}）";
            return null;
        }

        long length;
        try
        {
            length = new FileInfo(full).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = "读不到文件信息";
            return null;
        }

        if (length == 0)
        {
            reason = "空文件";
            return null;
        }

        if (length > MaxBytes)
        {
            reason = $"文件过大（上限 {MaxBytes / 1024 / 1024}MB）";
            return null;
        }

        return full;
    }
}
