namespace Beautiful.Core.Animation;

/// <summary>
/// 打开/关闭动画的单帧输出（归一化矩形 + 整体不透明度）。
/// <para>
/// 【为什么与条带帧不同型】genie 需要每条带各自的矩形来表现弯曲；打开/关闭是<b>整块窗口</b>
/// 的缩放淡入淡出，一个矩形就够。渲染侧拿到后乘源宽高即得像素，与 <see cref="GenieRect"/>
/// 同一套归一化口径（左上 (0,0)、右下 (1,1)）。
/// </para>
/// </summary>
public readonly record struct OpenCloseFrame(GenieRect Rect, double Opacity);
