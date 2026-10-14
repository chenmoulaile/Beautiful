using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush; // 消歧：UseWindowsForms 隐式 using 带入 System.Drawing.Brush
using Brushes = System.Windows.Media.Brushes; // 同上：System.Drawing.Brushes
using HorizontalAlignment = System.Windows.HorizontalAlignment; // 消歧：WinForms 隐式 using 带入同名枚举
using UserControl = System.Windows.Controls.UserControl; // 消歧：WinForms 同名 UserControl

namespace Beautiful.App.TopBar;

/// <summary>
/// 顶栏时钟点击的日历浮层内容（Task 9）：当月网格 + 今天强调色高亮 + 上一月/下一月切换。
/// 由 <see cref="Controls.FluentPopupHost"/> 承载（共享背板/圆角规则）。短暂生命周期：
/// 构造期取当前主题资源，随弹层关闭整体回收，无需订阅 ThemeChanged。
/// </summary>
public partial class CalendarFlyout : UserControl
{
    private static readonly string[] WeekNames = { "日", "一", "二", "三", "四", "五", "六" };
    private const double Cell = 32; // 固定单元格边长：Deterministic DesiredSize（宿主以 Measure(inf,inf) 同步测量，
                                    // 星号列/Auto 行在无限约束下会塌缩 → 必须用固定尺寸，否则弹层高度算少被裁）

    private readonly DateTime _today = DateTime.Today;
    private DateTime _displayMonth; // 当前显示的"月首"

    public CalendarFlyout()
    {
        InitializeComponent();
        _displayMonth = new DateTime(_today.Year, _today.Month, 1);
        BuildWeekHeader();
        Rebuild();
    }

    private void OnPrev(object sender, RoutedEventArgs e) { _displayMonth = _displayMonth.AddMonths(-1); Rebuild(); }
    private void OnNext(object sender, RoutedEventArgs e) { _displayMonth = _displayMonth.AddMonths(1); Rebuild(); }

    private Brush TextBrush() => (Brush?)TryFindResource("ThemeTextPrimary") ?? Brushes.White;
    private Brush AccentBrush() => (Brush?)TryFindResource("ThemeAccent") ?? Brushes.DodgerBlue;

    private void BuildWeekHeader()
    {
        var fg = TextBrush();
        for (var c = 0; c < 7; c++)
        {
            WeekHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Cell) });
            var t = new TextBlock
            {
                Text = WeekNames[c],
                FontSize = 11,
                Opacity = 0.7,
                Width = Cell,
                TextAlignment = TextAlignment.Center,
                Foreground = fg,
            };
            Grid.SetColumn(t, c);
            WeekHeader.Children.Add(t);
        }
    }

    private void Rebuild()
    {
        MonthLabel.Text = $"{_displayMonth.Year}年{_displayMonth.Month}月";
        DayGrid.Children.Clear();
        DayGrid.RowDefinitions.Clear();
        DayGrid.ColumnDefinitions.Clear();

        var accent = AccentBrush();
        var fg = TextBrush();

        for (var c = 0; c < 7; c++)
            DayGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Cell) });
        for (var r = 0; r < 6; r++)
            DayGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Cell) });

        // 该月 1 号落在周几（周日=0）→ 前置空位，网格按周日起排（与表头一致）。
        var firstOfMonth = new DateTime(_displayMonth.Year, _displayMonth.Month, 1);
        int leading = (int)firstOfMonth.DayOfWeek;
        int days = DateTime.DaysInMonth(_displayMonth.Year, _displayMonth.Month);
        for (int i = 0; i < days; i++)
        {
            var date = firstOfMonth.AddDays(i);
            int cell = leading + i;
            DayGrid.Children.Add(BuildDayCell(date.Day, date.Date == _today, accent, fg, cell / 7, cell % 7));
        }
    }

    private Border BuildDayCell(int day, bool isToday, Brush accent, Brush fg, int row, int col)
    {
        var border = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(isToday ? 14 : 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = isToday ? accent : Brushes.Transparent,
            Child = new TextBlock
            {
                Text = day.ToString(),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                // 今天用白字压强调色圆底；其余继承主题前景（宿主已铺 tint/模糊兜底，无黑）。
                Foreground = isToday ? Brushes.White : fg,
            }
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, col);
        return border;
    }
}
