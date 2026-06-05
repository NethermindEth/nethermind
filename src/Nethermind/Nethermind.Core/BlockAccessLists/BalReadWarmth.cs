// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Per-transaction EIP-2929 warm/cold tracking for BAL-declared storage reads, keyed by the dense read
/// ordinal of <see cref="BalReadStoragePlan"/> instead of a <c>JournalSet&lt;StorageCell&gt;</c>. Turns the
/// SLOAD warm-set probe from a 52-byte <see cref="StorageCell"/> hash add/lookup into a single bit test.
/// </summary>
/// <remarks>
/// Warmth is a journaled bitset. <see cref="WarmUp"/> sets the ordinal's bit and appends it to the revert
/// journal; <see cref="TakeSnapshot"/> returns the journal high-water mark and <see cref="Restore"/> un-warms
/// every ordinal warmed after the snapshot - mirroring <c>JournalSet&lt;T&gt;.TakeSnapshot</c>/<c>Restore</c>
/// so the warm/cold gas charge stays byte-exact across sub-frame reverts. The EIP-2929 tracing exception
/// (warmth is intentionally NOT rolled back while tracing access) is honored by the caller, which skips
/// <see cref="Restore"/> exactly as <c>StackAccessTracker.Restore</c> already does for the cell set.
/// <para>
/// The journal entries <c>journal[0..journalCount)</c> are exactly the currently-warm ordinals in warming
/// order with no duplicates (a bit is journaled only on the cold-&gt;warm transition), so the journal never
/// needs more than <c>readCount</c> slots. Backing arrays are pooled; <see cref="Reset"/> reuses the instance
/// across transactions. The bulk clear in <see cref="Reset"/> is the only vectorizable step and relies on the
/// runtime's SIMD <see cref="System.Span{T}"/> clear; the per-access set/test and the scattered revert
/// un-warm are inherently scalar bit operations.
/// </para>
/// </remarks>
public sealed class BalReadWarmth : IDisposable
{
    private ulong[] _warm;
    private int[] _journal;
    private int _journalCount;
    private readonly int _wordCount;

    /// <param name="readCount">Size of the ordinal space (the block's total declared storage reads).</param>
    public BalReadWarmth(int readCount)
    {
        _wordCount = (readCount + 63) >> 6;
        _warm = _wordCount == 0 ? [] : ArrayPool<ulong>.Shared.Rent(_wordCount);
        // Rented arrays carry the previous tenant's contents; the bitset must start cleared.
        _warm.AsSpan(0, _wordCount).Clear();
        _journal = readCount == 0 ? [] : ArrayPool<int>.Shared.Rent(readCount);
        _journalCount = 0;
    }

    /// <summary>Whether the read <paramref name="ordinal"/> has already been warmed this transaction.</summary>
    public bool IsWarm(int ordinal) => (_warm[ordinal >> 6] & (1UL << (ordinal & 63))) != 0;

    /// <summary>
    /// Warms the read <paramref name="ordinal"/>. Returns <c>true</c> if it was cold - the first access this
    /// transaction, to be charged as a cold access - and records it for revert; <c>false</c> if already warm.
    /// </summary>
    public bool WarmUp(int ordinal)
    {
        ref ulong word = ref _warm[ordinal >> 6];
        ulong mask = 1UL << (ordinal & 63);
        if ((word & mask) != 0) return false;
        word |= mask;
        _journal[_journalCount++] = ordinal;
        return true;
    }

    /// <summary>Journal high-water mark for a later <see cref="Restore"/> (mirrors <c>JournalSet.TakeSnapshot</c>).</summary>
    public int TakeSnapshot() => _journalCount;

    /// <summary>Un-warms every ordinal warmed after <paramref name="snapshot"/> on sub-frame revert.</summary>
    public void Restore(int snapshot)
    {
        for (int i = snapshot; i < _journalCount; i++)
        {
            int ordinal = _journal[i];
            _warm[ordinal >> 6] &= ~(1UL << (ordinal & 63));
        }
        _journalCount = snapshot;
    }

    /// <summary>Clears all warmth and the journal so the instance can be reused for the next transaction.</summary>
    public void Reset()
    {
        _warm.AsSpan(0, _wordCount).Clear();
        _journalCount = 0;
    }

    public void Dispose()
    {
        ulong[] warm = _warm;
        int[] journal = _journal;
        _warm = [];
        _journal = [];
        if (warm.Length > 0) ArrayPool<ulong>.Shared.Return(warm);
        if (journal.Length > 0) ArrayPool<int>.Shared.Return(journal);
    }
}
