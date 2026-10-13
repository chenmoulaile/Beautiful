using Beautiful.Core.Timeline;
using Xunit;

public class PlaybackClockTests
{
    private class FakeStopwatch : IStopwatch
    {
        public TimeSpan Elapsed { get; set; }
        public void Restart() => Elapsed = TimeSpan.Zero;
    }

    [Fact]
    public void 播放中_位置随时间推进()
    {
        var sw = new FakeStopwatch();
        var clock = new PlaybackClock(sw);
        clock.Sync(TimeSpan.FromSeconds(10), playing: true);
        sw.Elapsed = TimeSpan.FromSeconds(2);
        Assert.Equal(12, clock.CurrentPosition.TotalSeconds);
    }

    [Fact]
    public void 暂停_位置冻结()
    {
        var sw = new FakeStopwatch();
        var clock = new PlaybackClock(sw);
        clock.Sync(TimeSpan.FromSeconds(10), true);
        sw.Elapsed = TimeSpan.FromSeconds(3);
        clock.SetPlaying(false);            // 冻结在 13s
        sw.Elapsed = TimeSpan.FromSeconds(9);
        Assert.Equal(13, clock.CurrentPosition.TotalSeconds);
        clock.SetPlaying(true);             // 从 13s 继续
        sw.Elapsed = TimeSpan.FromSeconds(1);
        Assert.Equal(14, clock.CurrentPosition.TotalSeconds);
    }

    [Fact]
    public void Seek_重置基准()
    {
        var sw = new FakeStopwatch();
        var clock = new PlaybackClock(sw);
        clock.Sync(TimeSpan.Zero, true);
        sw.Elapsed = TimeSpan.FromSeconds(100);
        clock.Seek(TimeSpan.FromSeconds(50));
        Assert.Equal(50, clock.CurrentPosition.TotalSeconds);
    }

    [Fact]
    public void Sync_再次调用_重新基准化()
    {
        var sw = new FakeStopwatch();
        var clock = new PlaybackClock(sw);
        clock.Sync(TimeSpan.FromSeconds(10), true);
        sw.Elapsed = TimeSpan.FromSeconds(5);
        clock.Sync(TimeSpan.FromSeconds(11), true);   // 外部校准
        sw.Elapsed = TimeSpan.Zero;
        Assert.Equal(11, clock.CurrentPosition.TotalSeconds);
    }

    [Fact]
    public void Sync_负位置_钳为零()
    {
        var clock = new PlaybackClock(new FakeStopwatch());
        clock.Sync(TimeSpan.FromSeconds(-5), true);
        Assert.Equal(TimeSpan.Zero, clock.CurrentPosition);
    }

    [Fact]
    public void Estimate_SMTCPaused推进()
    {
        var last = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);
        var now = last.AddSeconds(7);
        var est = PlaybackClock.Estimate(TimeSpan.FromSeconds(30), last, playing: true, nowUtc: now);
        Assert.Equal(37, est.TotalSeconds);
        var paused = PlaybackClock.Estimate(TimeSpan.FromSeconds(30), last, playing: false, nowUtc: now);
        Assert.Equal(30, paused.TotalSeconds);
    }
}
