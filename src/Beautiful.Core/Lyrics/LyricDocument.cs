namespace Beautiful.Core.Lyrics;

public record LyricDocument(IReadOnlyList<LyricLine> Lines, string Source, bool HasTranslation);
