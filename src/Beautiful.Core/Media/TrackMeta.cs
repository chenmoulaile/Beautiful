namespace Beautiful.Core.Media;

/// <summary>
/// 当前曲目元数据（SMTC 会话投影）。原属已删除的逐行文本模块；v4 收敛后移到这里，
/// 媒体胶囊/正在播放面板/后续 macOS "正在播放"瓦片共用同一份 DTO。
/// </summary>
/// <param name="Title">标题（SMTC MediaProperties.Title）。</param>
/// <param name="Artist">艺术家。</param>
/// <param name="Album">专辑名。</param>
/// <param name="Duration">时长（TimelineProperties.EndTime）。</param>
/// <param name="CoverBytes">封面字节（无封面时为 null）。</param>
public record TrackMeta(
    string Title,
    string Artist,
    string Album,
    TimeSpan Duration,
    byte[]? CoverBytes = null);
