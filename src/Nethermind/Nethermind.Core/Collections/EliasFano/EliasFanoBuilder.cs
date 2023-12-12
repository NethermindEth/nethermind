// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Nethermind.Core.Collections.EliasFano;

/// <summary>
///     Creates a new builder to build the elias fano encoded structure from
///     monotonic increasing numbers
///     - `universe`: The (exclusive) upper bound of integers to be stored, i.e., an integer in `[0..universe - 1]`.
///     - `numValues`: The number of integers that will be pushed (> 0).
///     Number of integers should be more that 0
/// </summary>
public struct EliasFanoBuilder
{
    private BitVector _highBits;
    private BitVector _lowBits;
    private readonly ulong _universe;
    private readonly int _numValues;
    private int _pos = 0;
    private ulong _last = 0;
    private readonly int _lowLen;

    public EliasFanoBuilder(ulong universe, int numValues)
    {
        if (numValues == 0) throw new EliasFanoBuilderException($"the number of values:{numValues} > 0");

        _universe = universe;
        _numValues = numValues;

        ulong temp = universe / (ulong)numValues;
        _lowLen = 63 - BitOperations.LeadingZeroCount(temp);
        _highBits = new BitVector(numValues + 1 + (int)(universe >> _lowLen) + 1);
        _lowBits = new BitVector();
    }

    /// <summary>
    ///     Pushes integer `val` at the end.
    /// </summary>
    /// <param name="val">Pushed integer that must be no less than the last one.</param>
    /// <exception cref="ArgumentException"></exception>
    public void Push(ulong val)
    {
        if (val < _last)
            throw new EliasFanoBuilderException($"val:{val} < _last:{_last}");

        if (_universe < _last)
            throw new EliasFanoBuilderException($"_universe:{_universe} < _last:{_last})");

        if (_numValues <= _pos)
            throw new EliasFanoBuilderException($"_numValues:{_numValues} <= _pos:{_pos})");

        _last = val;
        ulong lowMask = ((ulong)1 << _lowLen) - 1;

        if (_lowLen != 0) _lowBits.PushBits(val & lowMask, _lowLen);
        _highBits.SetBit((int)(val >> _lowLen) + _pos, true);
        _pos += 1;
    }

    public void Extend(IEnumerable<ulong> values)
    {
        foreach (ulong val in values)
        {
            Push(val);
        }
    }

    public EliasFano Build()
    {
        return new EliasFano(new DArray(_highBits), _lowBits, _lowLen, _universe);
    }
}
