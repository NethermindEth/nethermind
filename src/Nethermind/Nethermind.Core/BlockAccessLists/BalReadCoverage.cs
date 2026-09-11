// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>Tracks one worker's block-wide reads and distinct chargeable reads in its current transaction.</summary>
/// <remarks>
/// Single-writer during execution. Reads survive EVM reverts under EIP-7928.
/// The owning plan releases the arrays after workers finish; callers must not retain them across blocks.
/// </remarks>
public sealed class BalReadCoverage
{
    // The first _wordCount words hold block coverage; the next _wordCount words deduplicate the transaction.
    private ulong[] _bits;
    private readonly int _wordCount;
    private readonly ArrayPool<ulong> _pool;
    private readonly List<int> _touchedWords = [];
    private Address? _lastAddress;
    private UInt256 _lastSlot;
    private int _lastOrdinal;

    /// <summary>The owning block plan, or null after it is disposed.</summary>
    public BalReadStoragePlan? Plan { get; private set; }

    /// <summary>Distinct non-system declared reads in the current transaction.</summary>
    public ulong ChargeableReadCount { get; private set; }

    internal BalReadCoverage(BalReadStoragePlan plan) : this(plan, ArrayPool<ulong>.Shared) { }

    internal BalReadCoverage(BalReadStoragePlan plan, ArrayPool<ulong> pool)
    {
        Plan = plan;
        _pool = pool;
        _wordCount = (int)(((long)plan.TotalReads + 63) / 64);
        _bits = _wordCount == 0 ? [] : pool.Rent(2 * _wordCount);
        _bits.AsSpan(0, 2 * _wordCount).Clear();
    }

    /// <summary>Starts the next transaction, retaining coverage of earlier transactions.</summary>
    public void StartSlice()
    {
        foreach (int word in _touchedWords) _bits[_wordCount + word] = 0;
        _touchedWords.Clear();
        ChargeableReadCount = 0;
        _lastAddress = null;
    }

    /// <summary>Marks a declared read, returning false for slots outside the read plan.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryMark(in StorageCell cell)
    {
        ObjectDisposedException.ThrowIf(Plan is null, this);
        if (cell.Address == _lastAddress && cell.Index == _lastSlot) return _lastOrdinal >= 0;
        Plan!.TryGetOrdinal(cell, out _lastOrdinal);
        _lastAddress = cell.Address;
        _lastSlot = cell.Index;
        int ordinal = _lastOrdinal;
        if (ordinal < 0) return false;
        int word = ordinal >> 6;
        ulong mask = 1UL << (ordinal & 63);
        if ((_bits[_wordCount + word] & mask) == 0)
        {
            if (_bits[_wordCount + word] == 0) _touchedWords.Add(word);
            _bits[_wordCount + word] |= mask;
            _bits[word] |= mask;
            if (Plan.IsChargeable(word, mask)) ChargeableReadCount++;
        }
        return true;
    }

    internal void Absorb(BalReadCoverage other)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i <= _wordCount - Vector<ulong>.Count; i += Vector<ulong>.Count)
                (new Vector<ulong>(_bits, i) | new Vector<ulong>(other._bits, i)).CopyTo(_bits, i);
        }
        for (; i < _wordCount; i++) _bits[i] |= other._bits[i];
    }

    internal int FirstUncovered()
    {
        int count = Plan!.TotalReads;
        for (int i = 0; i < _wordCount; i++)
        {
            ulong mask = count - i * 64 >= 64 ? ulong.MaxValue : (1UL << (count & 63)) - 1;
            ulong missing = ~_bits[i] & mask;
            if (missing != 0) return i * 64 + BitOperations.TrailingZeroCount(missing);
        }
        return -1;
    }

    internal void Release()
    {
        ulong[] bits = _bits;
        Plan = null;
        _bits = [];
        _lastAddress = null;
        _touchedWords.Clear();
        _touchedWords.Capacity = 0;
        if (bits.Length != 0) _pool.Return(bits, clearArray: true);
    }
}
