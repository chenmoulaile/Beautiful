using System.Text.Json;

namespace Beautiful.Core.Lyrics;

public sealed class LyricCacheService
{
    private readonly string _dir;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public LyricCacheService(string? rootDir = null)
        => _dir = Path.Combine(rootDir ?? AppContext.BaseDirectory, "cache", "lyrics");

    public LyricDocument? TryGet(string key)
    {
        try
        {
            var path = Path.Combine(_dir, key + ".json");
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<LyricDocument>(File.ReadAllText(path), JsonOpts);
        }
        catch { try { File.Delete(Path.Combine(_dir, key + ".json")); } catch { } return null; }
    }

    public void Put(string key, LyricDocument doc)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, key + ".json"), JsonSerializer.Serialize(doc, JsonOpts));
        }
        catch { /* 缓存失败不影响主流程 */ }
    }
}
