// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P;

/// <summary>Shares transaction hashes while retaining independent, bounded knowledge for each peer.</summary>
/// <remarks>
/// Peers remember slot generations rather than hashes. Replacing a shared slot advances its generation,
/// so eviction can cause an extra announcement, but cannot suppress a different transaction.
/// Odd generations mark replacements in progress. Readers validate the complete hash against an even
/// generation before marking it known. A delayed reader can overwrite a newer peer marker, causing
/// an extra announcement, but generations never identify two different hashes in the same slot.
/// </remarks>
internal sealed class TransactionHashCache
{
    private const int Ways = 8;
    private readonly Entry[] _entries;
    private readonly SetState[] _sets;
    private long _insertionEpoch;

    internal TransactionHashCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        int sets = capacity == 0 ? 0 : checked((int)BitOperations.RoundUpToPowerOf2((uint)((capacity - 1) / Ways + 1)));
        _entries = new Entry[checked(sets * Ways)];
        _sets = new SetState[sets];
        for (int i = 0; i < sets; i++) _sets[i].Gate = new SpinLock(enableThreadOwnerTracking: false);
    }

    internal PeerCache CreatePeerCache() => new(this);

    private bool Set(in ValueHash256 hash, long[] knownGenerations)
    {
        if (_entries.Length == 0) return true;

        long hashCode = hash.GetHashCode64();
        int set = (int)hashCode & (_sets.Length - 1);
        int start = set * Ways;
        Span<Entry> entries = _entries.AsSpan(start, Ways);
        Span<long> generations = knownGenerations.AsSpan(start, Ways);
        for (int i = 0; i < entries.Length; i++)
        {
            ref Entry entry = ref entries[i];
            long generation = Volatile.Read(ref entry.Generation);
            if (generation == 0 || (generation & 1) != 0 || Volatile.Read(ref entry.HashCode) != hashCode) continue;

            // Match the core caches' seqlock read ordering on ARM64; the 256-bit key can tear.
            if (!Sse.IsSupported) Interlocked.MemoryBarrier();
            ValueHash256 storedHash = entry.Hash;
            if (!Sse.IsSupported) Interlocked.MemoryBarrier();
            if (Volatile.Read(ref entry.Generation) != generation || !storedHash.Equals(in hash)) continue;

            Refresh(ref entry);
            return MarkKnown(ref generations[i], generation);
        }

        ref SetState state = ref _sets[set];
        bool lockTaken = false;
        try
        {
            state.Gate.Enter(ref lockTaken);
            int oldest = 0;
            long oldestSeen = long.MaxValue;
            for (int i = 0; i < entries.Length; i++)
            {
                ref Entry entry = ref entries[i];
                if (entry.Generation != 0 && entry.Hash.Equals(in hash))
                {
                    Refresh(ref entry);
                    return MarkKnown(ref generations[i], entry.Generation);
                }

                if (entry.LastSeen < oldestSeen)
                {
                    oldest = i;
                    oldestSeen = entry.LastSeen;
                }
            }

            ref Entry replacement = ref entries[oldest];
            long nextGeneration = checked(replacement.Generation + 2);
            Interlocked.Exchange(ref replacement.Generation, nextGeneration - 1);
            replacement.Hash = hash;
            replacement.HashCode = hashCode;
            replacement.LastSeen = Interlocked.Increment(ref _insertionEpoch);
            Volatile.Write(ref replacement.Generation, nextGeneration);
            return MarkKnown(ref generations[oldest], nextGeneration);
        }
        finally
        {
            if (lockTaken) state.Gate.Exit();
        }
    }

    private void Refresh(ref Entry entry)
    {
        // Repeated fan-out hits need not dirty shared cache lines until another hash is inserted.
        long epoch = Volatile.Read(ref _insertionEpoch);
        if (Volatile.Read(ref entry.LastSeen) != epoch) Volatile.Write(ref entry.LastSeen, epoch);
    }

    private static bool MarkKnown(ref long knownGeneration, long generation) =>
        Volatile.Read(ref knownGeneration) != generation && Interlocked.Exchange(ref knownGeneration, generation) != generation;

    internal sealed class PeerCache(TransactionHashCache owner)
    {
        private readonly long[] _knownGenerations = new long[owner._entries.Length];

        internal bool Set(in ValueHash256 hash) => owner.Set(in hash, _knownGenerations);
    }

    private struct Entry
    {
        public ValueHash256 Hash;
        public long HashCode;
        public long Generation;
        public long LastSeen;
    }

    // Separate gates for cache lines up to 128 bytes wide.
    [StructLayout(LayoutKind.Sequential, Size = 128)]
    private struct SetState
    {
        public SpinLock Gate;
    }
}
