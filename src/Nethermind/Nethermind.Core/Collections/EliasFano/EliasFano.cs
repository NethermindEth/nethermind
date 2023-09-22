// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections.EliasFano;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Collections.EliasFano;

public readonly struct EliasFanoS
{
    public readonly DArray _highBits;
    public readonly BitVector _lowBits;
    public readonly int _lowLen;
    public readonly ulong _universe;

    public EliasFanoS(DArray highBits, BitVector lowBits, int lowLen, ulong universe)
    {
        _highBits = highBits;
        _lowBits = lowBits;
        _lowLen = lowLen;
        _universe = universe;
    }

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
}

public struct EliasFano
{
    private BitVector _highBits;
    private BitVector _lowBits;
    private readonly ulong _universe;
    private readonly int _numValues;
    private int _pos;
    private ulong _last;
    private readonly int _lowLen;

    public EliasFano(ulong universe, int numValues)
    {
        int lowLen = (int)Math.Ceiling(Math.Log2(universe / (ulong)numValues));
        _highBits = new BitVector((numValues + 1) + (int)(universe >> lowLen) + 1);
        _lowBits = new BitVector();
        _universe = universe;
        _numValues = numValues;
        _pos = 0;
        _last = 0;
        _lowLen = lowLen;
    }


    public void Push(ulong val)
    {
        if (val < _last) throw new ArgumentException("not allowed");
        if (_universe < _last) throw new ArgumentException("not allowed");
        if (_numValues <= _pos) throw new ArgumentException("not allowed");

        _last = val;
        ulong lowMask = (((ulong)1) << _lowLen) - 1;

        if (_lowLen != 0)
        {
            _lowBits.PushBits(val & lowMask, _lowLen);
        }
        _highBits.SetBit((int)(val >> _lowLen) + _pos, true);
        _pos += 1;
    }

    public EliasFanoS Build()
    {
        return new EliasFanoS(new DArray(_highBits), _lowBits, _lowLen, _universe);
    }
}
