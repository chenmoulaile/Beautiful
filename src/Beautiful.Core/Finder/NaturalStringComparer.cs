using System;
using System.Collections.Generic;

namespace Beautiful.Core.Finder;

/// <summary>
/// Finder 名称列的自然排序比较器（Phase 6-a）。
/// <para>
/// 排序口径固定为：ASCII 数字串按数值比较（<c>file2</c> &lt; <c>file10</c>），非数字段先用
/// <see cref="StringComparison.OrdinalIgnoreCase"/> 比较；整串在忽略大小写与数字前导零后仍等价时，
/// 才用 <see cref="StringComparison.Ordinal"/> 做最终决胜。这样大小写差异只影响完全并列的条目，
/// 不会让 <c>File10</c> 因为大写前缀就排到 <c>file2</c> 前面，同时同一输入在任意机器上次序一致。
/// </para>
/// <para>
/// 前导零只参与最终决胜：<c>file02</c> 与 <c>file2</c> 数值相等，原始串按序数比较时
/// <c>file02</c> 在前。数字段与非数字段相遇时按首字符序数比较，因此 <c>1</c> 排在 <c>a</c> 之前。
/// </para>
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    /// <summary>无状态单例，可直接传给 <see cref="Array.Sort{T}(T[], IComparer{T})"/> 或 LINQ。</summary>
    public static NaturalStringComparer Instance { get; } = new();

    private NaturalStringComparer()
    {
    }

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var i = 0;
        var j = 0;

        while (i < x.Length && j < y.Length)
        {
            var xDigit = IsAsciiDigit(x[i]);
            var yDigit = IsAsciiDigit(y[j]);

            if (xDigit && yDigit)
            {
                var xStart = i;
                var yStart = j;
                while (i < x.Length && IsAsciiDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && IsAsciiDigit(y[j]))
                {
                    j++;
                }

                var cmp = CompareDigitRuns(
                    x.AsSpan(xStart, i - xStart),
                    y.AsSpan(yStart, j - yStart));
                if (cmp != 0)
                {
                    return cmp;
                }

                continue;
            }

            if (xDigit != yDigit)
            {
                // 一侧数字、一侧非数字：按首字符序数比较（'1' < 'a'），不做任何文化映射。
                return x[i].CompareTo(y[j]);
            }

            var xRunStart = i;
            var yRunStart = j;
            while (i < x.Length && !IsAsciiDigit(x[i]))
            {
                i++;
            }

            while (j < y.Length && !IsAsciiDigit(y[j]))
            {
                j++;
            }

            var runCmp = x.AsSpan(xRunStart, i - xRunStart)
                .CompareTo(y.AsSpan(yRunStart, j - yRunStart), StringComparison.OrdinalIgnoreCase);
            if (runCmp != 0)
            {
                return runCmp;
            }
        }

        if (i < x.Length)
        {
            return 1;
        }

        if (j < y.Length)
        {
            return -1;
        }

        // 忽略大小写、数字值都相等：原始串序数比较给唯一且稳定的次序。
        return string.Compare(x, y, StringComparison.Ordinal);
    }

    private static int CompareDigitRuns(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
    {
        // 数值比较忽略前导零；先比有效位数，再逐位比。
        var xStart = 0;
        var yStart = 0;
        while (xStart < x.Length - 1 && x[xStart] == '0')
        {
            xStart++;
        }

        while (yStart < y.Length - 1 && y[yStart] == '0')
        {
            yStart++;
        }

        var xLength = x.Length - xStart;
        var yLength = y.Length - yStart;
        if (xLength != yLength)
        {
            return xLength < yLength ? -1 : 1;
        }

        for (var k = 0; k < xLength; k++)
        {
            if (x[xStart + k] != y[yStart + k])
            {
                return x[xStart + k] < y[yStart + k] ? -1 : 1;
            }
        }

        return 0;
    }

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';
}
