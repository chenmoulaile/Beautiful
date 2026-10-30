namespace Beautiful.Core.Finder;

/// <summary>
/// Finder 菜单/快捷键可触发的命令（Phase 6-a）。枚举值只表达语义，不绑定具体 WPF 控件。
/// </summary>
public enum FinderCommand
{
    /// <summary>未识别或空手势。</summary>
    None,

    Copy,

    Cut,

    Paste,

    SelectAll,

    NewWindow,

    NewFolder,

    NewTab,

    CloseTab,

    Back,

    Forward,

    IconView,

    ListView,

    ColumnView,

    GalleryView,

    Rename,

    Delete,

    EmptyTrash,

    QuickLook,

    ToggleHidden,

    GoToFolder,

    Search,

    GetInfo,

    Cancel
}

/// <summary>
/// 手势串 → <see cref="FinderCommand"/> 的纯映射（Phase 6-a）。
/// <para>
/// 规范化：去空白、转小写、按 <c>+</c> 分段，修饰键顺序无关。Ctrl 即 macOS 的 ⌘（Command），
/// 因此 <c>ctrl+c</c>、<c>cmd+c</c>、<c>⌘+c</c> 等价。未知手势一律返回 <see cref="FinderCommand.None"/>，
/// 不抛异常——键盘事件来自系统，不能因为多按一个键让窗口崩掉。
/// </para>
/// </summary>
public static class FinderKeyMap
{
    /// <summary>把形如 <c>ctrl+shift+n</c> 的手势映射为命令。</summary>
    /// <param name="gesture">手势串，大小写/空白/修饰键顺序均不敏感。</param>
    /// <returns>对应命令；null、空串、未知组合返回 <see cref="FinderCommand.None"/>。</returns>
    public static FinderCommand Map(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return FinderCommand.None;
        }

        var ctrl = false;
        var alt = false;
        var shift = false;
        string? key = null;

        foreach (var raw in gesture.Split('+'))
        {
            var token = raw.Trim().ToLowerInvariant();
            if (token.Length == 0)
            {
                continue;
            }

            switch (token)
            {
                case "ctrl":
                case "control":
                case "cmd":
                case "command":
                case "⌘":
                    ctrl = true;
                    break;
                case "alt":
                case "option":
                case "opt":
                    alt = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                default:
                    key = token;
                    break;
            }
        }

        if (key is null)
        {
            return FinderCommand.None;
        }

        // 固定修饰键顺序，保证 "shift+ctrl+n" 与 "ctrl+shift+n" 命中同一分支。
        var canonical = $"{(ctrl ? "ctrl+" : string.Empty)}" +
                        $"{(alt ? "alt+" : string.Empty)}" +
                        $"{(shift ? "shift+" : string.Empty)}" +
                        key;

        return canonical switch
        {
            "ctrl+c" => FinderCommand.Copy,
            "ctrl+x" => FinderCommand.Cut,
            "ctrl+v" => FinderCommand.Paste,
            "ctrl+a" => FinderCommand.SelectAll,
            "ctrl+n" => FinderCommand.NewWindow,
            "ctrl+shift+n" => FinderCommand.NewFolder,
            "ctrl+t" => FinderCommand.NewTab,
            "ctrl+w" => FinderCommand.CloseTab,
            "ctrl+[" => FinderCommand.Back,
            "ctrl+]" => FinderCommand.Forward,
            "ctrl+1" => FinderCommand.IconView,
            "ctrl+2" => FinderCommand.ListView,
            "ctrl+3" => FinderCommand.ColumnView,
            "ctrl+4" => FinderCommand.GalleryView,
            "enter" => FinderCommand.Rename,
            "f2" => FinderCommand.Rename,
            "backspace" => FinderCommand.Delete,
            "shift+backspace" => FinderCommand.EmptyTrash,
            "space" => FinderCommand.QuickLook,
            "ctrl+shift+." => FinderCommand.ToggleHidden,
            "ctrl+shift+g" => FinderCommand.GoToFolder,
            "ctrl+f" => FinderCommand.Search,
            "ctrl+i" => FinderCommand.GetInfo,
            "escape" => FinderCommand.Cancel,
            _ => FinderCommand.None
        };
    }
}
