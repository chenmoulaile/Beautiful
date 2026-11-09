using Beautiful.Core.Animation;
using Xunit;

/// <summary>
/// GenieMesh 拓扑（wp-borrow 收口补测，2026-11-10）：顶点数/索引数/下标行主序/索引覆盖完整/UV 角点。
/// 拓扑一次算好、每帧只写位置是帧预算（P95 &lt; 20ms）的前提——数错了顶点缓冲就错位，锁住关键值。
/// </summary>
public class GenieMeshTopologyTests
{
    [Fact]
    public void 网格尺寸_顶点数与索引数_默认与钳制()
    {
        Assert.Equal((24, 10), GenieMesh.ClampGrid(GenieMesh.DefaultRows, GenieMesh.DefaultColumns));
        Assert.Equal(25 * 11, GenieMesh.VertexCount(GenieMesh.DefaultRows, GenieMesh.DefaultColumns));  // 275
        Assert.Equal(24 * 10 * 6, GenieMesh.TriangleIndexCount(GenieMesh.DefaultRows, GenieMesh.DefaultColumns)); // 1440
        // 钳制：至少 1×1，至多 96×96
        Assert.Equal((1, 1), GenieMesh.ClampGrid(0, -3));
        Assert.Equal((96, 96), GenieMesh.ClampGrid(500, 500));
    }

    [Fact]
    public void 顶点下标_行主序()
    {
        Assert.Equal(0, GenieMesh.VertexIndex(0, 0, 10));
        Assert.Equal(11, GenieMesh.VertexIndex(1, 0, 10));   // 换行跳 columns+1
        Assert.Equal(5, GenieMesh.VertexIndex(0, 5, 10));
        Assert.Equal(274, GenieMesh.VertexIndex(24, 10, 10)); // 右下角 = (r+1)*(c+1)-1
    }

    [Fact]
    public void 三角索引_长度吻合_每格六条_边界含右下角()
    {
        const int rows = 24, columns = 10;
        var idx = GenieMesh.BuildTriangleIndices(rows, columns);
        Assert.Equal(GenieMesh.TriangleIndexCount(rows, columns), idx.Length);
        // 最后一个格子的 6 条索引：右下角顶点 (24,10) 必须出现
        Assert.Contains(GenieMesh.VertexIndex(rows, columns, columns), idx);
        // 全部索引都在合法顶点范围内（越界 = 渲染侧读越顶点缓冲）
        var maxVertex = GenieMesh.VertexCount(rows, columns);
        foreach (var i in idx) Assert.True(i >= 0 && i < maxVertex);
    }

    [Fact]
    public void 纹理坐标_四角0到1_与顶点一一对应()
    {
        const int rows = 24, columns = 10;
        var uvs = GenieMesh.BuildTextureCoordinates(rows, columns);
        Assert.Equal(GenieMesh.VertexCount(rows, columns), uvs.Length);
        Assert.Equal((0.0, 0.0), uvs[GenieMesh.VertexIndex(0, 0, columns)]);
        Assert.Equal((1.0, 0.0), uvs[GenieMesh.VertexIndex(0, columns, columns)]);
        Assert.Equal((0.0, 1.0), uvs[GenieMesh.VertexIndex(rows, 0, columns)]);
        Assert.Equal((1.0, 1.0), uvs[GenieMesh.VertexIndex(rows, columns, columns)]);
        Assert.Equal((0.5, 0.5), uvs[GenieMesh.VertexIndex(12, 5, columns)]);  // 24/10 网格的中点
    }
}
