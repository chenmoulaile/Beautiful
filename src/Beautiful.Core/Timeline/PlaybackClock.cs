namespace Beautiful.Core.Timeline;

public sealed class PlaybackClock(IStopwatch? stopwatch = null)
{
    private readonly IStopwatch _sw = stopwatch ?? new RealStopwatch();
    private TimeSpan _basePos;
    private bool _playing = true;

    public bool IsPlaying => _playing;

    public void Sync(TimeSpan position, bool playing)
    {
        _basePos = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        _playing = playing;
        _sw.Restart();
    }

    public void SetPlaying(bool playing)
    {
        if (_playing == playing) return;
        if (!playing) _basePos = CurrentPosition;   // 冻结在当前推算位置
        _sw.Restart();
        _playing = playing;
    }

    public void Seek(TimeSpan position) => Sync(position, _playing);

    public TimeSpan CurrentPosition => _playing ? _basePos + _sw.Elapsed : _basePos;

    public static TimeSpan Estimate(TimeSpan timelinePosition, DateTime lastUpdatedUtc, bool playing, DateTime? nowUtc = null)
        => playing
            ? timelinePosition + ((nowUtc ?? DateTime.UtcNow) - lastUpdatedUtc)
            : timelinePosition;
}
