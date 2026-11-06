using Beautiful.Core.Themes;
using Xunit;

/// <summary>
/// icon-theme.json 纯解析：信封（版本 + Overrides）坏了才整体失败，坏条目逐条丢弃；
/// 键统一小写、值收两种形状（asset:图集键 / 本地文件路径）、Normalize 收敛去重与上限。
/// </summary>
public class IconThemeFileTests
{
    [Fact]
    public void 合法JSON解析出覆盖表()
    {
        var ok = IconThemeFile.TryParse(
            """
            { "Version": 1, "Overrides": { "Code": "asset:code", "finder": "C:\\icons\\finder.png" } }
            """,
            out var data, out var errors);

        Assert.True(ok);
        Assert.Empty(errors);
        Assert.Equal(2, data!.Overrides.Count);
        // 键统一小写
        Assert.Equal("asset:code", data.Overrides["code"]);
        Assert.Equal(@"C:\icons\finder.png", data.Overrides["finder"]);
    }

    [Fact]
    public void Roundtrip序列化再解析一致()
    {
        var map = new Dictionary<string, string> { ["code"] = "asset:code", ["finder"] = @"C:\i\f.png" };
        var original = IconThemeFile.Normalize(new IconThemeData(IconThemeFile.CurrentVersion, map));
        var json = IconThemeFile.Serialize(original);

        Assert.True(IconThemeFile.TryParse(json, out var parsed, out var errors));
        Assert.Empty(errors);
        Assert.Equal(original.Overrides, parsed!.Overrides);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("不是JSON")]
    [InlineData("[1,2,3]")]
    [InlineData("{ \"Overrides\": {} }")]                       // 缺 Version
    [InlineData("{ \"Version\": 2, \"Overrides\": {} }")]       // 版本不支持
    [InlineData("{ \"Version\": true, \"Overrides\": {} }")]    // 版本不是数字
    [InlineData("{ \"Version\": 1 }")]                          // 缺 Overrides
    [InlineData("{ \"Version\": 1, \"Overrides\": [] }")]       // Overrides 不是对象
    public void 信封坏了整体失败(string? json)
    {
        Assert.False(IconThemeFile.TryParse(json, out var data, out var errors));
        Assert.Null(data);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void 坏条目逐条丢弃其余保留()
    {
        var json = """
            { "Version": 1, "Overrides": {
                "code": "asset:code",
                "bad_empty_value": "",
                "bad_long_key__aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa": "asset:code",
                "bad_asset": "asset:",
                "bad_asset_chars": "asset:LEJKOSD!",
                "bad_json_value": 42,
                "finder": "C:\\icons\\finder.png"
            } }
            """;

        Assert.True(IconThemeFile.TryParse(json, out var data, out var errors));
        Assert.NotNull(data);
        Assert.Equal(2, data!.Overrides.Count);
        Assert.Equal("asset:code", data.Overrides["code"]);
        Assert.Equal(@"C:\icons\finder.png", data.Overrides["finder"]);
        Assert.Equal(5, errors.Count);   // 每个坏条目一条原因
    }

    [Fact]
    public void Normalize去重后者胜并钳上限()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CODE"] = "asset:code",
            ["code"] = "asset:calculator",   // 归一小写后重复 → 后者胜
            ["empty"] = "   ",
            ["bad"] = "asset:不合法!",
        };
        var normalized = IconThemeFile.Normalize(new IconThemeData(IconThemeFile.CurrentVersion, map));

        // 存活只有 code 一条：CODE/code 归一后是同一个键（后者胜），empty 与 bad 形状非法被丢弃。
        Assert.Single(normalized.Overrides);
        Assert.Equal("asset:calculator", normalized.Overrides["code"]);
        Assert.False(normalized.Overrides.ContainsKey("bad"));
    }

    [Fact]
    public void Normalize钳制条目上限()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < IconThemeFile.MaxOverrides + 100; i++)
        {
            map[$"key{i}"] = "asset:code";
        }
        var normalized = IconThemeFile.Normalize(new IconThemeData(IconThemeFile.CurrentVersion, map));
        Assert.Equal(IconThemeFile.MaxOverrides, normalized.Overrides.Count);
    }

    [Theory]
    [InlineData("asset:finder", null)]
    [InlineData("asset:ms-teams", null)]
    [InlineData("asset:trash_empty", null)]
    [InlineData("C:\\icons\\x.png", null)]
    [InlineData("asset:", "长度")]
    [InlineData("asset:LEJKO!", "非法字符")]
    [InlineData("asset:this-key-is-way-too-long-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "长度")]
    [InlineData("a\nb", "非法字符")]
    public void 值形状校验(string value, string? expectedPart)
    {
        var reason = IconThemeFile.ValidateValueShape(value);
        if (expectedPart is null) Assert.Null(reason);
        else Assert.Contains(expectedPart, reason);
    }
}
