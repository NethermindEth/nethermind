// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>Tracks one worker's block-wide reads and distinct chargeable reads in its current transaction.</summary>
/// <remarks>
/// Single-writer during execution. Reads survive EVM reverts under EIP-7928.
/// The owning plan releases the arrays after workers finish; callers must not retain them across blocks.
/// </remarks>
public sealed class BalReadCoverage
{
    private ulong[] _block;
    private ulong[] _slice;
    private readonly List<int> _touchedWords = [];
    private StorageCell _lastCell;
    private int _lastOrdinal;
    private bool _hasLastCell;

    /// <summary>The owning block plan, or null after it is disposed.</summary>
    public BalReadStoragePlan? Plan { get; private set; }

    /// <summary>Distinct non-system declared reads in the current transaction.</summary>
    public ulong ChargeableReadCount { get; private set; }

    internal BalReadCoverage(BalReadStoragePlan plan)
    {
        Plan = plan;
        int words = (int)(((long)plan.TotalReads + 63) / 64);
        _block = new ulong[words];
        _slice = new ulong[words];
    }

    /// <summary>Starts the next transaction, retaining coverage of earlier transactions.</summary>
    public void StartSlice()
    {
        foreach (int word in _touchedWords) _slice[word] = 0;
        _touchedWords.Clear();
        ChargeableReadCount = 0;
        _hasLastCell = false;
    }

    /// <summary>Marks a declared read, returning false for slots outside the read plan.</summary>
    public bool TryMark(in StorageCell cell)
    {
        if (!_hasLastCell || !_lastCell.Equals(cell))
        {
            Plan!.TryGetOrdinal(cell, out _lastOrdinal);
            _lastCell = cell;
            _hasLastCell = true;
        }
        int ordinal = _lastOrdinal;
        if (ordinal < 0) return false;
        int word = ordinal >> 6;
        ulong mask = 1UL << (ordinal & 63);
        if ((_slice[word] & mask) == 0)
        {
            if (_slice[word] == 0) _touchedWords.Add(word);
            _slice[word] |= mask;
            _block[word] |= mask;
            if (cell.Address != Eip7002Constants.WithdrawalRequestPredeployAddress
                && cell.Address != Eip7251Constants.ConsolidationRequestPredeployAddress)
                ChargeableReadCount++;
        }
        return true;
    }

    internal void Absorb(BalReadCoverage other)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i <= _block.Length - Vector<ulong>.Count; i += Vector<ulong>.Count)
                (new Vector<ulong>(_block, i) | new Vector<ulong>(other._block, i)).CopyTo(_block, i);
        }
        for (; i < _block.Length; i++) _block[i] |= other._block[i];
    }

    internal int FirstUncovered(int count)
    {
        for (int i = 0; i < _block.Length; i++)
        {
            ulong mask = count - i * 64 >= 64 ? ulong.MaxValue : (1UL << (count & 63)) - 1;
            ulong missing = ~_block[i] & mask;
            if (missing != 0) return i * 64 + BitOperations.TrailingZeroCount(missing);
        }
        return -1;
    }

    internal void Release()
    {
        Plan = null;
        _block = [];
        _slice = [];
        _touchedWords.Clear();
        _touchedWords.TrimExcess();
    }
}
