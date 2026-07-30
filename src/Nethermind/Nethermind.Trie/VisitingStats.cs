// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Trie
{
    /// <summary>
    /// Per-call counters populated by <see cref="MeteredTrieNodeResolver"/> while a single
    /// <see cref="PatriciaTree.Accept{TNodeContext}"/> traversal runs with a non-null
    /// <c>diagnostics</c> argument. Values aggregate across the account and any storage tries
    /// visited during that traversal.
    /// </summary>
    /// <remarks>
    /// Counter mutations use <see cref="Interlocked"/> so the same instance is safe to share
    /// across multiple threads when the visitor runs with <c>MaxDegreeOfParallelism &gt; 1</c>
    /// (e.g. the <c>BatchedTrieVisitor</c> path). For the default proof-RPC code path the
    /// traversal is single-threaded and the atomic ops are uncontended.
    /// <para>
    /// Reader properties read each counter independently, so <see cref="CacheHits"/> is not a
    /// consistent snapshot of <see cref="NodeLookups"/> and <see cref="CacheMisses"/> if read
    /// concurrently with ongoing mutations. Read after the traversal completes.
    /// </para>
    /// </remarks>
    public sealed class VisitingStats
    {
        private long _nodeLookups;
        private long _cacheMisses;
        private int _maxDepth;

        /// <summary>
        /// Total number of <c>FindCachedOrUnknown</c> calls observed by the metered resolver
        /// during the traversal — i.e. one per visited trie node.
        /// </summary>
        public long NodeLookups => Interlocked.Read(ref _nodeLookups);

        /// <summary>
        /// Number of <c>LoadRlp</c> / <c>TryLoadRlp</c> calls observed — i.e. node fetches that
        /// missed the in-process trie store cache and required reading from the underlying store.
        /// </summary>
        public long CacheMisses => Interlocked.Read(ref _cacheMisses);

        /// <summary>
        /// Deepest <see cref="TreePath.Length"/> (in nibbles) reached during the traversal across
        /// the account or any storage trie.
        /// </summary>
        public int MaxDepth => Volatile.Read(ref _maxDepth);

        /// <summary>
        /// Derived: <see cref="NodeLookups"/> minus <see cref="CacheMisses"/>, clamped at zero.
        /// </summary>
        /// <remarks>
        /// The clamp guards a pathological case where a caller invokes <c>LoadRlp</c> without a
        /// preceding <c>FindCachedOrUnknown</c>; the proof code path always pairs them, so under
        /// normal use the subtraction never goes negative.
        /// </remarks>
        public long CacheHits => Math.Max(0, NodeLookups - CacheMisses);

        /// <summary>Increment <see cref="NodeLookups"/> by one. Thread-safe.</summary>
        public void RecordLookup() => Interlocked.Increment(ref _nodeLookups);

        /// <summary>Increment <see cref="CacheMisses"/> by one. Thread-safe.</summary>
        public void RecordCacheMiss() => Interlocked.Increment(ref _cacheMisses);

        /// <summary>
        /// Update <see cref="MaxDepth"/> to <paramref name="level"/> if it exceeds the current value.
        /// Thread-safe (CAS loop).
        /// </summary>
        public void ObserveDepth(int level)
        {
            int current = _maxDepth;
            while (level > current)
            {
                int observed = Interlocked.CompareExchange(ref _maxDepth, level, current);
                if (observed == current) return;
                current = observed;
            }
        }
    }
}
