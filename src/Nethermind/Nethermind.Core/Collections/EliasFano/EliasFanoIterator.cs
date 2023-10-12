// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;

namespace Nethermind.Core.Collections.EliasFano;

public class EliasFanoIterator : IEnumerator<ulong>
{
    private readonly int _chunksInWord;
    private readonly ulong _lowMask;
    private int _chunksAvail;
    private EliasFano _ef;
    private UnaryIter? _highIter;
    private int _k;
    private ulong _lowBuf;

    public EliasFanoIterator(EliasFano ef, int k)
    {
        _ef = ef;
        _k = k;
        _lowBuf = 0;
        _lowMask = ((ulong)1 << ef._lowLen) - 1;

        if (ef._lowLen != 0)
        {
            _chunksInWord = 64 / ef._lowLen;
            _chunksAvail = 0;
        }
        else
        {
            _chunksInWord = 0;
            _chunksAvail = ef.Length;
        }

        _highIter = null;
        if (k < _ef.Length)
        {
            int pos = _ef._highBits.SelectSet(k)!.Value;
            _highIter = new UnaryIter(ef._highBits._data, pos);
        }
    }

    public void Dispose()
    {
        // TODO release managed resources here
    }

    public bool MoveNext()
    {
        if (_k == _ef._highBits.NumOnes) _highIter = null;

        if (_highIter is not null)
        {
            if (_chunksAvail == 0)
            {
                _lowBuf = _ef._lowBits.GetWord64(_k * _ef._lowLen)!.Value;
                _chunksAvail = _chunksInWord - 1;
            }
            else
                _chunksAvail -= 1;

            _highIter.MoveNext();
            int high = _highIter.Current;
            ulong low = _lowBuf & _lowMask;
            ulong ret = ((ulong)(high - _k) << _ef._lowLen) | low;
            _k += 1;
            _lowBuf >>= _ef._lowLen;
            Current = ret;
            return true;
        }

        return false;
    }

    public void Reset()
    {
        throw new NotImplementedException();
    }

    public ulong Current { get; private set; }

    object IEnumerator.Current => Current;
}

public class UnaryIter : IEnumerator<int>
{
    private readonly BitVector _bv;
    private readonly int _startingPos;
    private ulong _buf;
    public int _pos;

    public UnaryIter(BitVector bv, int pos)
    {
        _bv = bv;
        _startingPos = _pos = pos;
        _buf = _bv.Words[_pos / BitVector.WordLen] & (ulong.MaxValue << (_pos % BitVector.WordLen));
    }

    public int Current => _pos;

    public void Dispose()
    {
        // TODO release managed resources here
    }

    public bool MoveNext()
    {
        ulong buf = _buf;
        while (buf == 0)
        {
            _pos += BitVector.WordLen;
            int wordPos = _pos / BitVector.WordLen;
            if (_bv.Words.Count <= wordPos) return false;
            buf = _bv.Words[wordPos];
        }

        int posInWord = BitOperations.TrailingZeroCount(buf);
        _buf = buf & (buf - 1);
        _pos = (_pos & ~(BitVector.WordLen - 1)) + posInWord;
        return true;
    }

    public void Reset()
    {
        _pos = _startingPos;
        _buf = _bv.Words[_pos / BitVector.WordLen] & (ulong.MaxValue << (_pos % BitVector.WordLen));
    }

    object IEnumerator.Current => Current;
}
