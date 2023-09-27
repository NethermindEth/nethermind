// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core.Collections.EliasFano;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Collections.EliasFano;

public struct EliasFano
{
    public const int LinearScanThreshold= 64;
    public DArray _highBits;
    public BitVector _lowBits;
    public int _lowLen;
    public ulong _universe;

    public int Length => _highBits.NumOnes;

    public EliasFano(DArray highBits, BitVector lowBits, int lowLen, ulong universe)
    {
        _highBits = highBits;
        _lowBits = lowBits;
        _lowLen = lowLen;
        _universe = universe;
    }

    /// <summary>
    ///
    ///
    /// </summary>
    /// <param name="pos"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public int Rank(ulong pos)
    {
        if (_universe < pos) throw new ArgumentException();
        if (_universe == pos) return _highBits._indexS1.NumPositions;

        int hRank = (int)(pos >> _lowLen);
        int hPos = _highBits.Select0(hRank)!.Value;
        int rank = hPos - hRank;

        ulong lPos = pos & (((ulong)1 << _lowLen) - 1);

        while ((hPos > 0)
               && _highBits._data.GetBit(hPos-1)!.Value
               && (_lowBits.GetBits((rank-1)*_lowLen, _lowLen) >= lPos))
        {
            rank -= 1;
            hPos -= 1;
        }

        return rank;
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="k"></param>
    /// <returns></returns>
    public ulong? Delta(int k)
    {
        if (Length <= k) return null;

        int highVal = _highBits.Select1(k)!.Value;
        ulong lowVal = _lowBits.GetBits(k * _lowLen, _lowLen)!.Value;

        ulong x = 0;
        if (k != 0)
        {
            int temp = highVal - _highBits._data.Predecessor1(highVal - 1)!.Value - 1;
            x = ((ulong)temp << _lowLen) + lowVal - _lowBits.GetBits((k - 1) * _lowLen, _lowLen)!.Value;
        }
        else
        {
            x = (((ulong)highVal) << _lowLen) | lowVal;
        }

        return x;
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="start"></param>
    /// <param name="end"></param>
    /// <param name="val"></param>
    /// <returns></returns>
    public int? BinSearchRange(int start, int end, ulong val)
    {
        if (start < end) return null;

        int hi = end;
        int lo = start;

        while ((hi - lo)>LinearScanThreshold)
        {
            int mi = (lo + hi) / 2;
            ulong x = Select(mi)!.Value;
            if (val == x) return mi;
            if (val < x)
            {
                hi = mi;
            }
            else
            {
                lo = mi + 1;
            }
        }

        EliasFanoIterator it = new EliasFanoIterator(this, lo);
        for (int i = lo; i < hi; i++)
        {
            it.MoveNext();
            ulong x = it.Current;
            if (val == x) return i;
        }

        return null;
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="k"></param>
    /// <returns></returns>
    public ulong? Select(int k)
    {
        if (Length <= k) return null;

        ulong temp = (ulong)(_highBits.Select1(k)!.Value - k) << _lowLen;
        temp |= _lowBits.GetBits(k * _lowLen, _lowLen)!.Value;
        return temp;
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="pos"></param>
    /// <returns></returns>
    public ulong? Predecessor(ulong pos)
    {
        if (_universe <= pos) return null;

        int rank = Rank(pos + 1);
        return rank > 0 ? Select(rank - 1) : null;
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="pos"></param>
    /// <returns></returns>
    public ulong? Successor(ulong pos)
    {
        if (_universe <= pos) return null;

        int rank = Rank(pos);
        return rank < Length ? Select(rank) : null;
    }

    public IEnumerable<ulong> GetEnumerator(int k)
    {
        EliasFanoIterator itr = new EliasFanoIterator(this, k);
        while (itr.MoveNext()) yield return itr.Current;
    }
}
