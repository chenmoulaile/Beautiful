using System;
using System.Windows;
using System.Windows.Controls;
using Beautiful.Core.Finder;
using Panel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;

namespace Beautiful.App.Finder;

/// <summary>
/// 图标视图的网格面板（Phase 6-b）：布局完全由 <see cref="FinderIconGrid"/> 决定。
/// <para>
/// 不自己发明一套算术——Measure/Arrange 与框选命中、方向键移动共用 Core 的同一份度量，
/// 因此"看到的格子位置"与"点中的格子索引"永远一致。这也是不用 WrapPanel 的原因：
/// WrapPanel 的换行规则不可外部查询，框选只能靠视觉树反查，索引与几何会各算一遍。
/// </para>
/// <para>
/// 面板不做虚拟化（访达目录本身已被 <c>FinderWindow</c> 截断到 1500 项），
/// 换来的是框选几何的绝对可控；图标位图仍由窗口后台限流加载，滚动不会触发磁盘调用。
/// </para>
/// </summary>
public sealed class FinderIconPanel : Panel
{
    /// <summary>单元格宽（含间隙）。</summary>
    public static readonly DependencyProperty CellWidthProperty = DependencyProperty.Register(
        nameof(CellWidth), typeof(double), typeof(FinderIconPanel),
        new FrameworkPropertyMetadata(FinderIconGrid.DefaultCellWidth,
            FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>单元格高（含间隙）。</summary>
    public static readonly DependencyProperty CellHeightProperty = DependencyProperty.Register(
        nameof(CellHeight), typeof(double), typeof(FinderIconPanel),
        new FrameworkPropertyMetadata(FinderIconGrid.DefaultCellHeight,
            FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>四周留白。</summary>
    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(double), typeof(FinderIconPanel),
        new FrameworkPropertyMetadata(FinderIconGrid.DefaultPadding,
            FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>最近一次布局的度量（框选/命中测试复用；未布局时为默认值）。</summary>
    public FinderGridMetrics Metrics { get; private set; }

    public double CellWidth
    {
        get => (double)GetValue(CellWidthProperty);
        set => SetValue(CellWidthProperty, value);
    }

    public double CellHeight
    {
        get => (double)GetValue(CellHeightProperty);
        set => SetValue(CellHeightProperty, value);
    }

    public double Padding
    {
        get => (double)GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) || double.IsNaN(availableSize.Width)
            ? 0
            : availableSize.Width;
        Metrics = FinderIconGrid.Measure(width, InternalChildren.Count, CellWidth, CellHeight, Padding);

        var childSize = new Size(Metrics.CellWidth, Metrics.CellHeight);
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(childSize);
        }

        return new Size(Metrics.ContentWidth, Metrics.ContentHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // 以最终宽度重算一次：滚动条出现/消失会改变可用宽度，行数随之变化。
        Metrics = FinderIconGrid.Measure(finalSize.Width, InternalChildren.Count, CellWidth, CellHeight, Padding);

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var (x, y) = FinderIconGrid.OriginOf(i, Metrics, Padding);
            InternalChildren[i].Arrange(new Rect(x, y, Metrics.CellWidth, Metrics.CellHeight));
        }

        return new Size(Math.Max(finalSize.Width, Metrics.ContentWidth),
            Math.Max(finalSize.Height, Metrics.ContentHeight));
    }
}
