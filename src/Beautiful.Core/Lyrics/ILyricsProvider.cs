namespace Beautiful.Core.Lyrics;

public record TrackMeta(string Title, string Artist, string Album, TimeSpan Duration, byte[]? CoverBytes = null);

public record LyricFetch(IReadOnlyList<LrcLine> Original, IReadOnlyList<LrcLine>? Translation, string Source);

public interface ILyricsProvider
{
    string Name { get; }
    Task<LyricFetch?> FetchAsync(TrackMeta meta, CancellationToken ct);
}
