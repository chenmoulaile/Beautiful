using System.Windows;
using System.Windows.Controls;

namespace Beautiful.App.Dock;

/// <summary>Dock 条目模板选择：Kind=Media 用媒体胶囊模板，其余（Pinned/Window/RecycleBin）用通用图标模板。</summary>
public sealed class DockItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? DefaultTemplate { get; set; }
    public DataTemplate? MediaTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        => item is DockItem { Kind: DockItemKind.Media } ? MediaTemplate : DefaultTemplate;
}
