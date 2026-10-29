// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>Tracks the declared storage reads one worker executed in the block.</summary>
/// <remarks>
/// Single-writer during execution. Reads survive EVM reverts under EIP-7928.
/// The owning plan releases the arrays after workers finish; callers must not retain them across blocks.
/// </remarks>
public sealed class BalReadCoverage
{
    private ulong[] _bits;
    private readonly int _wordCount;
    private readonly ArrayPool<ulong> _pool;
    private Address? _lastAddress;
    private UInt256 _lastSlot;
    private int _lastOrdinal;

    /// <summary>The owning block plan, or null after it is disposed.</summary>
    public BalReadStoragePlan? Plan { get; private set; }

    internal BalReadCoverage(BalReadStoragePlan plan) : this(plan, ArrayPool<ulong>.Shared) { }

    internal BalReadCoverage(BalReadStoragePlan plan, ArrayPool<ulong> pool)
    {
        Plan = plan;
        _pool = pool;
        _wordCount = (int)(((long)plan.TotalReads + 63) / 64);
        _bits = _wordCount == 0 ? [] : pool.Rent(_wordCount);
        _bits.AsSpan(0, _wordCount).Clear();
    }

    /// <summary>Marks a declared read, returning false for slots outside the read plan.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryMark(in StorageCell cell)
    {
        ObjectDisposedException.ThrowIf(Plan is null, this);
        if (ReferenceEquals(cell.Address, _lastAddress) && cell.Index == _lastSlot) return _lastOrdinal >= 0;
        return MarkSlow(cell);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool MarkSlow(in StorageCell cell)
    {
        Plan!.TryGetOrdinal(cell, out _lastOrdinal);
        _lastAddress = cell.Address;
        _lastSlot = cell.Index;
        int ordinal = _lastOrdinal;
        if (ordinal < 0) return false;
        _bits[ordinal >> 6] |= 1UL << (ordinal & 63);
        return true;
    }

    internal bool IsCovered(int ordinal) => (_bits[ordinal >> 6] & (1UL << (ordinal & 63))) != 0;

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
        if (bits.Length != 0) _pool.Return(bits);
    }
}
