using System.Windows;
using System.Windows.Controls;
using Beautiful.Core.Dock;

namespace Beautiful.App.Dock;

/// <summary>
/// Dock 条目模板选择：Kind=Media 用媒体胶囊模板，Kind=Separator 用竖细线模板，
/// 其余（Pinned/Window/RecycleBin）用通用图标模板。
/// 分隔符必须有独立模板：它没有图标、没有运行指示、也没有悬停高亮，
/// 走图标模板会被当作"缺图标的条目"渲染成回退字形 ▣。
/// </summary>
public sealed class DockItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? DefaultTemplate { get; set; }
    public DataTemplate? MediaTemplate { get; set; }
    public DataTemplate? SeparatorTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        DockItem { Kind: DockItemKind.Media } => MediaTemplate,
        // 分隔符模板未提供（旧主题字典缺键）时收起为 0 宽条目：宁可看不见也不画成方块图标
        DockItem { Kind: DockItemKind.Separator } => SeparatorTemplate ?? DefaultTemplate,
        _ => DefaultTemplate
    };
}
