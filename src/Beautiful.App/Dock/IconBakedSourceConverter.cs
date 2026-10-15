using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Beautiful.App.Services;

namespace Beautiful.App.Dock;

/// <summary>
/// 条目图标 → 烘焙位图（v3 T6）。模板把 <c>Image.Source</c> 绑到条目本身，由本转换器交给
/// <see cref="IconBakeContext"/> 取（或烘）当前 (源, 尺寸, 主题, DPI) 下的冻结位图。
/// <para>
/// 【求值时机 = 频次上界】绑定只在**容器构建 / DataContext 更换**时求值：条目增删、标题刷新、
/// 主题与尺寸变更（DockWindow 会重建容器）。动画循环每帧只写 Left/Top/ScaleTransform，
/// 不会再经过这里，所以这条路径上的字典查找与一次性烘焙都不参与逐帧预算。
/// </para>
/// <para>
/// 【为什么不做异步】提取与烘焙都留在条目构建期（同步）。异步化会引入"迟到的图标挂到已被回收的
/// 条目上"这一类竞态，而本任务的收益（256px 源 + 1:1 位图）已经在同步路径里拿满；
/// 冷启动多出的开销由 LRU 与提取缓存吸收（同一 exe 只提一次）。
/// </para>
/// </summary>
public sealed class IconBakedSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DockItem item ? IconBakeContext.Apply(item.IconSource, item.Icon) : null;

    /// <summary>单向绑定：写回不可能发生（Image.Source 在本模板里不是双向的）。</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("图标位图只读。");
}
