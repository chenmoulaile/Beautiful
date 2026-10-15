namespace Beautiful.Core.Launcher;

/// <summary>
/// 解析后的启动器触发组合：<b>修饰键 + 一个主键名</b>（主键是小写规范名，虚拟键码由 App 侧映射——
/// Core 不认识 Windows VK，这样这张表也才能在 Linux CI 上跑）。
/// </summary>
/// <param name="Key">主键名（"space" / "j" / "f5" / "enter" …，恒为 <see cref="LauncherHotkey.KeyNames"/> 之一）。</param>
public readonly record struct LauncherGesture(bool Ctrl, bool Alt, bool Win, bool Shift, string Key)
{
    /// <summary>落盘/回显用的规范串（修饰键固定顺序，避免 "alt+ctrl+space" 与 "ctrl+alt+space" 两种写法并存）。</summary>
    public string ToText()
    {
        var parts = new List<string>(5);
        if (Ctrl) parts.Add("ctrl");
        if (Alt) parts.Add("alt");
        if (Win) parts.Add("win");
        if (Shift) parts.Add("shift");
        parts.Add(Key);
        return string.Join('+', parts);
    }
}

/// <summary>
/// 启动器热键的解析与自愈（v3 T7）。
/// <para>
/// 【为什么这两条规则必须在 Core】热键错一次的代价是不对称的：一个<b>不带修饰键</b>的组合
/// （"space"、"j"）会让全局钩子在用户每次敲空格时弹出面板并把这次按键吞掉——那是"整台机器没法打字"级别的
/// 故障，而不是"我的启动器不好用"。所以"至少一个修饰键、主键必须认得"是<b>写进配置的硬约束</b>，
/// 由 <see cref="Beautiful.Core.Settings.AppSettings.Normalized"/> 在落盘/加载两侧统一把关，
/// 而不是只在钩子里挡一次（手改 JSON 绕过 UI 的那条路同样要挡）。
/// </para>
/// <para>
/// 【为什么不用 Win 键做默认】Win+Space 是系统输入法切换、Ctrl+Space 是 IME 中英切换的既有绑定，
/// 抢它们会让用户在其他所有应用里失去原功能（v3 裁定：默认给 <see cref="Default"/>，
/// 用户若自己填了 Win 组合则视为知情选择，不再拦）。
/// </para>
/// </summary>
public static class LauncherHotkey
{
    /// <summary>默认组合：系统未占用、不含 Win 键、需要两个修饰键（不易误触）。</summary>
    public const string Default = "ctrl+alt+space";

    /// <summary>认识的主键名（App 侧的 VK 映射与此表一一对应；不在表内的名字一律解析失败）。</summary>
    public static IReadOnlyList<string> KeyNames { get; } =
    [
        "space", "enter", "tab", "escape", "backspace",
        "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m",
        "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
        "f1", "f2", "f3", "f4", "f5", "f6", "f7", "f8", "f9", "f10", "f11", "f12",
        "left", "right", "up", "down", "home", "end", "pageup", "pagedown",
        "minus", "equal", "comma", "period", "slash", "backslash", "semicolon", "quote", "leftbracket", "rightbracket",
    ];

    /// <summary>
    /// 解析组合串：分隔符容忍 <c>+ - 空格</c>，大小写不敏感，别名（control/option/windows/cmd/super/esc…）归一。
    /// 返回 false 的情形：null/空白、没有任何修饰键、主键缺失、主键不认识、修饰键与主键之外还有多余 token。
    /// </summary>
    public static bool TryParse(string? raw, out LauncherGesture gesture)
    {
        gesture = default;
        var ctrl = false; var alt = false; var win = false; var shift = false;
        string? main = null;
        foreach (var token in (raw ?? string.Empty).Split('+', '-', ' '))
        {
            var name = token.Trim().ToLowerInvariant();
            if (name.Length == 0) continue;
            switch (name)
            {
                case "ctrl": case "control": ctrl = true; continue;
                case "alt": case "option": case "menu": alt = true; continue;
                case "win": case "windows": case "meta": case "super": case "cmd": win = true; continue;
                case "shift": shift = true; continue;
            }
            if (main is not null) return false;                 // 两个主键（"ctrl+a+b"）：不猜
            if (!KeyNames.Contains(name)) return false;         // 不认识的主键名
            main = name;
        }
        // 没修饰键的组合一律拒绝（见类型注释的不对称代价）
        if (!ctrl && !alt && !win && !shift) return false;
        if (main is null) return false;
        gesture = new LauncherGesture(ctrl, alt, win, shift, main);
        return true;
    }

    /// <summary>解析并回到规范串；不合法即回落 <see cref="Default"/>（配置自愈用，绝不写回一个脏串）。</summary>
    public static string Canonicalize(string? raw) => TryParse(raw, out var gesture) ? gesture.ToText() : Default;
}
