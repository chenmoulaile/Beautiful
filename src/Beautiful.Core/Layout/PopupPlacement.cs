namespace Beautiful.Core.Layout;

/// <summary>
/// 弹层内容自绘投影的留白（物理 px）。分层窗口的阴影只能画在自己的表面上，所以内容根元素
/// 通常带一圈 <c>Margin</c> 给投影让位 —— 于是<b>窗口外框比可见卡片大一圈</b>。
/// </summary>
/// <remarks>
/// 落位必须区分这两个矩形：锚点表达的是"卡片要贴在哪里"，而 <c>SetWindowPos</c> 摆的是窗口外框。
/// 直接把外框左上角贴到锚点，卡片就会整体右下偏移一圈投影宽度（200% 屏上约 40×36 物理 px），
/// 且偏移量随 DPI 放大 —— 这正是"点顶栏『窗口』，菜单跑到别处去了"的成因。
/// </remarks>
public readonly record struct PopupShadow(double Left, double Top, double Right, double Bottom)
{
    /// <summary>无投影留白（卡片即窗口）。</summary>
    public static PopupShadow None => new(0, 0, 0, 0);

    /// <summary>留白为负是调用错误（内容 Margin 写反了），钳到 0 而不是让卡片反向溢出。</summary>
    public double LeftPx => Math.Max(0, Math.Round(Left));
    public double TopPx => Math.Max(0, Math.Round(Top));
    public double RightPx => Math.Max(0, Math.Round(Right));
    public double BottomPx => Math.Max(0, Math.Round(Bottom));
}

/// <summary>落位结果：窗口外框左上角（物理 px）与是否发生左/上翻转。</summary>
public readonly record struct PopupPlacement(int WindowX, int WindowY, bool FlipX, bool FlipY);

/// <summary>
/// 弹层落位算术：给定"卡片期望锚点"、卡片尺寸、投影留白与工作区，算出窗口外框该摆在哪。
/// 纯函数，全部输入输出都是物理像素，便于按屏取数（裁定：几何唯一来源是所在显示器）。
/// </summary>
public static class PopupPlacer
{
    /// <param name="work">锚点所在显示器的工作区（物理 px）。</param>
    /// <param name="anchorX">卡片左上角期望 X（向下展开）。</param>
    /// <param name="anchorY">卡片左上角期望 Y（向下展开）。</param>
    /// <param name="flipX">翻转时卡片<b>右</b>边要贴住的 X（子菜单/右对齐场景）。</param>
    /// <param name="flipY">翻转时卡片<b>底</b>边要贴住的 Y（托盘上翻场景）。</param>
    /// <param name="cardWidth">可见卡片宽（= 窗口宽 − 左右留白）。</param>
    /// <param name="cardHeight">可见卡片高（= 窗口高 − 上下留白）。</param>
    /// <param name="edgeInset">工作区边缘最小留白。</param>
    public static PopupPlacement Place(
        PhysicalRect work,
        double anchorX,
        double anchorY,
        double flipX,
        double flipY,
        double cardWidth,
        double cardHeight,
        PopupShadow shadow,
        double edgeInset)
    {
        var sl = shadow.LeftPx;
        var st = shadow.TopPx;

        var minCardLeft = work.Left + edgeInset;
        var maxCardRight = work.Right - edgeInset;
        var minCardTop = work.Top + edgeInset;
        var maxCardBottom = work.Bottom - edgeInset;

        // 向下展开：卡片左上角贴锚点 ⇒ 窗口外框左上角要再减去一圈留白。
        var x = anchorX - sl;
        var flipXApplied = false;
        if (anchorX + cardWidth > maxCardRight)
        {
            // 向右放不下 ⇒ 卡片右边贴 flipX。卡片左边在窗口内 sl 处，故窗口左边 = flipX − sl − 卡片宽。
            x = flipX - sl - cardWidth;
            flipXApplied = true;
        }

        var y = anchorY - st;
        var flipYApplied = false;
        if (anchorY + cardHeight > maxCardBottom)
        {
            y = flipY - st - cardHeight;
            flipYApplied = true;
        }

        // 夹取以"卡片"为准（不是外框）：留白可以溢出屏幕，卡片不行。
        // 先右/下后左/上 ⇒ 卡片宽过高到过工作区时**左优先**保住左上角（菜单向左溢出等于没有菜单）。
        if (x + sl + cardWidth > maxCardRight) x = maxCardRight - sl - cardWidth;
        if (x + sl < minCardLeft) x = minCardLeft - sl;
        if (y + st + cardHeight > maxCardBottom) y = maxCardBottom - st - cardHeight;
        if (y + st < minCardTop) y = minCardTop - st;

        return new PopupPlacement((int)Math.Round(x), (int)Math.Round(y), flipXApplied, flipYApplied);
    }
}
