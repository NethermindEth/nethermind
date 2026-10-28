// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Collections;

/// <summary>Decides when a hash collection reused across rounds has stayed sparse long enough to shrink.</summary>
/// <remarks>
/// Clearing a non-empty <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/> or
/// <see cref="System.Collections.Generic.HashSet{T}"/> zeroes its whole bucket array, so a map that one large round
/// grew pays for that capacity on every later clear, however few entries those rounds add. Shrinking after a long run
/// of sparse clears bounds that cost by the entries used. A map that fills at least once per run keeps its capacity,
/// and one that fills again after a shrink regrows at most once per <see cref="SparseClearsBeforeTrim"/> clears.
/// </remarks>
internal struct SparseClearTrimmer
{
    /// <summary>A clear that finds the map at most this fraction full, as a shift of its capacity, counts as sparse.</summary>
    private const int SparseShift = 3;

    /// <summary>The consecutive sparse clears after which the map shrinks.</summary>
    internal const int SparseClearsBeforeTrim = 256;

    /// <summary>The capacity a map is never shrunk below for sparseness, since its bucket clear is already cheap.</summary>
    /// <remarks>
    /// A prime the hash collections size to exactly. A sparse map holds at most an eighth of its capacity, so the target
    /// is either this floor or at most a quarter of the capacity, and the shrink always lands below the current size.
    /// </remarks>
    internal const int MinCapacity = 131;

    private int _sparseClears;
    private int _peakCount;

    /// <summary>Records a clear of a map that held <paramref name="count"/> entries in <paramref name="capacity"/>.</summary>
    /// <returns>The capacity to shrink the now empty map to, or zero to keep it.</returns>
    public int OnClear(int count, int capacity)
    {
        // An empty map's clear zeroes nothing, so it neither shows the map is used nor that it is not.
        if (count == 0) return 0;

        if (capacity <= MinCapacity || count > capacity >> SparseShift)
        {
            _sparseClears = 0;
            _peakCount = 0;
            return 0;
        }

        _peakCount = Math.Max(_peakCount, count);
        if (++_sparseClears < SparseClearsBeforeTrim) return 0;

        int trimTo = Math.Max(_peakCount * 2, MinCapacity);
        _sparseClears = 0;
        _peakCount = 0;
        return trimTo;
    }

    /// <summary>Starts a new run after the map was shrunk for another reason.</summary>
    public void Reset()
    {
        _sparseClears = 0;
        _peakCount = 0;
    }
}
