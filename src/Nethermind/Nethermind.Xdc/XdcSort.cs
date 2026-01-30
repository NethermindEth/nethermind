// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Nethermind.Xdc;

/// <summary>
/// Port of XDC's Go's sort.Slice function and its underlying implementation.
/// https://vscode.dev/github/XinFinOrg/XDPoSChain/blob/dev-upgrade/common/sort/slice.go#L29
/// This implementation is required since the order of validators is decided by it in XDC.
/// </summary>
public static class XdcSort
{
    private struct LessSwap<T>
    {
        public Func<T, T, bool> Less;
        public IList<T> Data;

        public LessSwap(IList<T> data, Func<T, T, bool> less)
        {
            Data = data;
            Less = less;
        }

        public void Swap(int i, int j)
        {
            T temp = Data[i];
            Data[i] = Data[j];
            Data[j] = temp;
        }
    }

    /// <summary>
    /// Sorts the slice x given the provided less function.
    /// The sort is not guaranteed to be stable: equal elements may be reversed from their original order.
    /// </summary>
    public static void Slice<T>(IList<T> x, Func<T, T, bool> less)
    {
        if (x == null)
            throw new ArgumentNullException(nameof(x));
        if (less == null)
            throw new ArgumentNullException(nameof(less));

        int length = x.Count;
        var data = new LessSwap<T>(x, less);
        QuickSort_func(data, 0, length, MaxDepth(length));
    }

    private static int MaxDepth(int n)
    {
        int depth = 0;
        for (int i = n; i > 0; i >>= 1)
        {
            depth++;
        }
        return depth * 2;
    }

    private static void QuickSort_func<T>(LessSwap<T> data, int a, int b, int maxDepth)
    {
        while (b - a > 12)
        {
            if (maxDepth == 0)
            {
                HeapSort_func(data, a, b);
                return;
            }
            maxDepth--;
            int mlo, mhi;
            DoPivot_func(data, a, b, out mlo, out mhi);
            if (mlo - a < b - mhi)
            {
                QuickSort_func(data, a, mlo, maxDepth);
                a = mhi;
            }
            else
            {
                QuickSort_func(data, mhi, b, maxDepth);
                b = mlo;
            }
        }
        if (b - a > 1)
        {
            for (int i = a + 6; i < b; i++)
            {
                if (data.Less(data.Data[i], data.Data[i - 6]))
                {
                    data.Swap(i, i - 6);
                }
            }
            InsertionSort_func(data, a, b);
        }
    }

    private static void HeapSort_func<T>(LessSwap<T> data, int a, int b)
    {
        int first = a;
        int lo = 0;
        int hi = b - a;
        for (int i = (hi - 1) / 2; i >= 0; i--)
        {
            SiftDown_func(data, i, hi, first);
        }
        for (int i = hi - 1; i >= 0; i--)
        {
            data.Swap(first, first + i);
            SiftDown_func(data, lo, i, first);
        }
    }

    private static void DoPivot_func<T>(LessSwap<T> data, int low, int high, out int middleLow, out int middleHigh)
    {
        int m = (int)((uint)(low + high) >> 1);
        if (high - low > 40)
        {
            int s = (high - low) / 8;
            MedianOfThree_func(data, low, low + s, low + 2 * s);
            MedianOfThree_func(data, m, m - s, m + s);
            MedianOfThree_func(data, high - 1, high - 1 - s, high - 1 - 2 * s);
        }
        MedianOfThree_func(data, low, m, high - 1);
        int pivot = low;
        int a = low + 1;
        int c = high - 1;
        while (a < c && data.Less(data.Data[a], data.Data[pivot]))
        {
            a++;
        }
        int b = a;
        while (true)
        {
            while (b < c && !data.Less(data.Data[pivot], data.Data[b]))
            {
                b++;
            }
            while (b < c && data.Less(data.Data[pivot], data.Data[c - 1]))
            {
                c--;
            }
            if (b >= c)
            {
                break;
            }
            data.Swap(b, c - 1);
            b++;
            c--;
        }
        bool protect = high - c < 5;
        if (!protect && high - c < (high - low) / 4)
        {
            int d = 0;
            if (!data.Less(data.Data[pivot], data.Data[high - 1]))
            {
                data.Swap(c, high - 1);
                c++;
                d++;
            }
            if (!data.Less(data.Data[b - 1], data.Data[pivot]))
            {
                b--;
                d++;
            }
            if (!data.Less(data.Data[m], data.Data[pivot]))
            {
                data.Swap(m, b - 1);
                b--;
                d++;
            }
            protect = d > 1;
        }
        if (protect)
        {
            while (true)
            {
                while (a < b && !data.Less(data.Data[b - 1], data.Data[pivot]))
                {
                    b--;
                }
                while (a < b && data.Less(data.Data[a], data.Data[pivot]))
                {
                    a++;
                }
                if (a >= b)
                {
                    break;
                }
                data.Swap(a, b - 1);
                a++;
                b--;
            }
        }
        data.Swap(pivot, b - 1);
        middleLow = b - 1;
        middleHigh = c;
    }

    private static void InsertionSort_func<T>(LessSwap<T> data, int a, int b)
    {
        for (int i = a + 1; i < b; i++)
        {
            for (int j = i; j > a && data.Less(data.Data[j], data.Data[j - 1]); j--)
            {
                data.Swap(j, j - 1);
            }
        }
    }

    private static void SiftDown_func<T>(LessSwap<T> data, int low, int high, int first)
    {
        int root = low;
        while (true)
        {
            int child = 2 * root + 1;
            if (child >= high)
            {
                break;
            }
            if (child + 1 < high && data.Less(data.Data[first + child], data.Data[first + child + 1]))
            {
                child++;
            }
            if (!data.Less(data.Data[first + root], data.Data[first + child]))
            {
                return;
            }
            data.Swap(first + root, first + child);
            root = child;
        }
    }

    private static void MedianOfThree_func<T>(LessSwap<T> data, int m1, int m0, int m2)
    {
        if (data.Less(data.Data[m1], data.Data[m0]))
        {
            data.Swap(m1, m0);
        }
        if (data.Less(data.Data[m2], data.Data[m1]))
        {
            data.Swap(m2, m1);
            if (data.Less(data.Data[m1], data.Data[m0]))
            {
                data.Swap(m1, m0);
            }
        }
    }
}
