namespace Beautiful.Core.Animation;

/// <summary>
/// genie 三角网的<b>拓扑</b>（与进度无关的那部分）：顶点数、UV、三角形索引。
/// <para>
/// 拓扑一次算好、每帧只换顶点坐标，这是把 300 个顶点做成"每帧只写位置"的关键：
/// 索引与 UV 若每帧重建，动画期间的分配压力会直接变成掉帧（本项目的帧时间判据是 P95 &lt; 20ms）。
/// </para>
/// <para>
/// 网格是纯 2D 映射（每个顶点只算出 x/y，深度恒为 0），所以同一份拓扑既能喂给真正的
/// 纹理网格渲染，也能在离屏时用 2D 线框画出来说明"形状对不对"——不需要动用户的桌面。
/// </para>
/// </summary>
public static class GenieMesh
{
    /// <summary>默认纵向行数（参考实现的 tile 数区间是 8-96，取 24 足够平滑又不贵）。</summary>
    public const int DefaultRows = 24;

    /// <summary>默认横向列数。横向不需要很密（脖子是竖向变化的），10 列足够。</summary>
    public const int DefaultColumns = 10;

    /// <summary>钳制行列数：至少 1×1，至多 96×96（参考实现的上限），避免把 GPU 顶点缓冲撑爆。</summary>
    public static (int Rows, int Columns) ClampGrid(int rows, int columns)
    {
        var r = Math.Clamp(rows, 1, 96);
        var c = Math.Clamp(columns, 1, 96);
        return (r, c);
    }

    /// <summary>顶点总数 = (rows+1) × (columns+1)。</summary>
    public static int VertexCount(int rows, int columns)
    {
        var (r, c) = ClampGrid(rows, columns);
        return (r + 1) * (c + 1);
    }

    /// <summary>三角形索引数 = rows × columns × 6（每个格子两个三角形）。</summary>
    public static int TriangleIndexCount(int rows, int columns)
    {
        var (r, c) = ClampGrid(rows, columns);
        return r * c * 6;
    }

    /// <summary>第 <paramref name="row"/> 行第 <paramref name="column"/> 列顶点的线性下标。</summary>
    public static int VertexIndex(int row, int column, int columns) => row * (columns + 1) + column;

    /// <summary>
    /// 生成三角形索引（顺时针/逆时针由渲染侧决定，这里只保证每个格子两三角、覆盖完整）。
    /// </summary>
    public static int[] BuildTriangleIndices(int rows, int columns)
    {
        var (r, c) = ClampGrid(rows, columns);
        var indices = new int[r * c * 6];
        var at = 0;
        for (var y = 0; y < r; y++)
        {
            for (var x = 0; x < c; x++)
            {
                var topLeft = VertexIndex(y, x, c);
                var topRight = VertexIndex(y, x + 1, c);
                var bottomLeft = VertexIndex(y + 1, x, c);
                var bottomRight = VertexIndex(y + 1, x + 1, c);

                indices[at++] = topLeft;
                indices[at++] = bottomLeft;
                indices[at++] = topRight;

                indices[at++] = topRight;
                indices[at++] = bottomLeft;
                indices[at++] = bottomRight;
            }
        }
        return indices;
    }

    /// <summary>UV 与顶点一一对应：u 横向、v 纵向，都从 0 到 1。</summary>
    public static (double U, double V)[] BuildTextureCoordinates(int rows, int columns)
    {
        var (r, c) = ClampGrid(rows, columns);
        var uvs = new (double, double)[(r + 1) * (c + 1)];
        for (var y = 0; y <= r; y++)
            for (var x = 0; x <= c; x++)
                uvs[VertexIndex(y, x, c)] = ((double)x / c, (double)y / r);
        return uvs;
    }

    /// <summary>某个顶点的归一坐标（喂给 <see cref="GenieLamp.Vertex"/> 用）。</summary>
    public static (double Tx, double Ty) VertexParameter(int row, int column, int rows, int columns)
    {
        var (r, c) = ClampGrid(rows, columns);
        return ((double)column / c, (double)row / r);
    }
}
