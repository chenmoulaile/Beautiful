namespace Beautiful.Core.Timeline;

public interface IStopwatch
{
    TimeSpan Elapsed { get; }
    void Restart();
}

public sealed class RealStopwatch : IStopwatch
{
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    public TimeSpan Elapsed => _sw.Elapsed;
    public void Restart() => _sw.Restart();
}
