// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Core.Collections.EliasFano;

public struct EliasFano
{
    private const int LinearScanThreshold = 64;
    public readonly DArray _highBits;
    public BitVector _lowBits;
    public readonly int _lowLen;
    public readonly ulong _universe;

    public int Length => _highBits.NumOnes;

    public EliasFano(DArray highBits, BitVector lowBits, int lowLen, ulong universe)
    {
        _highBits = highBits;
        _lowBits = lowBits;
        _lowLen = lowLen;
        _universe = universe;
    }

    /// <summary>
    ///     Returns the number of integers less than `num`
    ///     null is `num` > Universe
    /// </summary>
    /// <param name="num"></param>
    /// <exception cref="ArgumentException"></exception>
    public int Rank(ulong num)
    {
        if (_universe < num) throw new ArgumentException();
        if (_universe == num) return _highBits._indexSet.NumPositions;

        int hRank = (int)(num >> _lowLen);
        int hPos = _highBits.SelectUnSet(hRank)!.Value;
        int rank = hPos - hRank;

        ulong lPos = num & (((ulong)1 << _lowLen) - 1);

        while (hPos > 0
               && _highBits._data.GetBit(hPos - 1)!.Value
               && _lowBits.GetBits((rank - 1) * _lowLen, _lowLen) >= lPos)
        {
            rank -= 1;
            hPos -= 1;
        }

        return rank;
    }

    /// <summary>
    ///     Gets the difference between the `k-1`-th and `k`-th integers
    /// </summary>
    /// <param name="k"></param>
    /// <returns></returns>
    public ulong? Delta(int k)
    {
        if (Length <= k) return null;

        int highVal = _highBits.SelectSet(k)!.Value;
        ulong lowVal = _lowBits.GetBits(k * _lowLen, _lowLen)!.Value;

        ulong x = 0;
        if (k != 0)
        {
            int temp = highVal - _highBits._data.PredecessorSet(highVal - 1)!.Value - 1;
            x = ((ulong)temp << _lowLen) + lowVal - _lowBits.GetBits((k - 1) * _lowLen, _lowLen)!.Value;
        }
        else
            x = ((ulong)highVal << _lowLen) | lowVal;

        return x;
    }

    /// <summary>
    ///     Finds the position `k` such that `select(k) == val` and `k` in start..end.
    ///     Note that, if there are multiple values of `val`, one of them is returned
    /// </summary>
    /// <param name="start">start of the range (included)</param>
    /// <param name="end">end of the range (not included)</param>
    /// <param name="val">number to search for</param>
    /// <returns></returns>
    public int? BinSearchRange(int start, int end, ulong val)
    {
        if (start >= end) return null;

        int hi = end;
        int lo = start;

        while (hi - lo > LinearScanThreshold)
        {
            int mi = (lo + hi) / 2;
            ulong x = Select(mi)!.Value;
            if (val == x) return mi;
            if (val < x)
                hi = mi;
            else
                lo = mi + 1;
        }

        EliasFanoIterator it = new(this, lo);
        for (int i = lo; i < hi; i++)
        {
            it.MoveNext();
            ulong x = it.Current;
            if (val == x) return i;
        }

        return null;
    }

    /// <summary>
    ///     Returns the position of the `k`-th smallest integer
    /// </summary>
    /// <param name="k"></param>
    /// <returns></returns>
    public ulong? Select(int k)
    {
        if (Length <= k) return null;

        ulong temp = (ulong)(_highBits.SelectSet(k)!.Value - k) << _lowLen;
        temp |= _lowBits.GetBits(k * _lowLen, _lowLen)!.Value;
        return temp;
    }

    /// <summary>
    ///     Gets the largest element `pred` such that `pred is less than equal to pos`
    /// </summary>
    /// <param name="num"></param>
    /// <returns></returns>
    public ulong? Predecessor(ulong num)
    {
        if (_universe <= num) return null;

        int rank = Rank(num + 1);
        return rank > 0 ? Select(rank - 1) : null;
    }

    /// <summary>
    ///     Gets the smallest element such that element is greater than or equal to pos
    /// </summary>
    /// <param name="num"></param>
    /// <returns></returns>
    public ulong? Successor(ulong num)
    {
        if (_universe <= num) return null;

        int rank = Rank(num);
        return rank < Length ? Select(rank) : null;
    }

    /// <summary>
    ///     Enumerate over integers in the list starting from kth integer
    /// </summary>
    /// <param name="k"></param>
    /// <returns></returns>
    public IEnumerable<ulong> GetEnumerator(int k)
    {
        EliasFanoIterator itr = new(this, k);
        while (itr.MoveNext()) yield return itr.Current;
    }
}
