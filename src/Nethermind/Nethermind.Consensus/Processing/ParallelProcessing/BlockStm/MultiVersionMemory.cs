// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Collections;

namespace Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;

/// <summary>
/// Tracks per-transaction reads and writes for the Block-STM scheduler. Used to serve
/// later txs' reads from earlier txs' writes, to flag re-execution dependencies, and to
/// validate a tx's read-set against the current memory state.
/// </summary>
public sealed class MultiVersionMemory(int txCount)
{
    public static readonly object SelfDestructMonit = new();

    /// <summary>
    /// Stored value for a given location: the writing tx's incarnation plus the data.
    /// </summary>
    public readonly record struct Value(int Incarnation, object Data)
    {
        /// <summary>Marker that the writing tx will be re-executed; readers must take a dep.</summary>
        public static readonly Value Estimate = new(-1, default!);

        public bool IsEstimate => Incarnation == -1;
    }

    // Per-tx write-sets. Each entry is a ConcurrentDictionary so the executing worker can
    // publish writes while higher txs read concurrently — lock-free on the hot read path.
    private readonly ConcurrentDictionary<ParallelStateKey, Value>[] _data = Enumerable.Range(0, txCount)
        .Select(_ => new ConcurrentDictionary<ParallelStateKey, Value>())
        .ToArray();

    // Per-key writer index: every ever-written location maps to the sorted-descending list
    // of tx indices that currently hold a write for it. TryRead consults this O(1) instead
    // of walking N prior tx-dicts. Maintenance happens in ApplyWriteSet only.
    private readonly ConcurrentDictionary<ParallelStateKey, WriterList> _keyWriters = new();

    // Locations written by the latest published incarnation of each tx — used to detect
    // removed keys on re-execution and to convert writes to estimates on validation abort.
    private readonly HashSet<ParallelStateKey>?[] _lastWrittenLocations = new HashSet<ParallelStateKey>?[txCount];

    // Reads recorded by the latest published incarnation of each tx — used by
    // ValidateReadSet to detect stale dependencies.
    private readonly HashSet<Read>?[] _lastReads = new HashSet<Read>?[txCount];

    // Pre-block system-contract writes (EIP-4788 beacon root, EIP-2935 blockhash). Seeded
    // once at block start and consulted as the last fallback before NotFound, so per-tx
    // reads see the freshly-written system state instead of stale trie-store values.
    // Returns Status.Ok with TxVersion.Empty so validation treats the value as stable —
    // a real later tx writing the same location naturally invalidates the read via the
    // standard version-mismatch path.
    private Dictionary<ParallelStateKey, object>? _systemOverlay;

    /// <summary>Seeds pre-block system-contract writes. Must be called before workers start.</summary>
    public void SeedSystemOverlay(Dictionary<ParallelStateKey, object> writes) => _systemOverlay = writes;

    /// <summary>
    /// Publishes a transaction's read- and write-set. Returns true when higher txs that
    /// already read this tx may need re-validation (any added, removed, or re-written
    /// key — the stored TxVersion advances even when the value is unchanged).
    /// </summary>
    public bool Record(TxVersion version, HashSet<Read> readSet, Dictionary<ParallelStateKey, object> writeSet)
    {
        bool writeSetChanged = ApplyWriteSet(version, writeSet);
        _lastReads[version.TxIndex] = readSet;
        return writeSetChanged;
    }

    private bool ApplyWriteSet(TxVersion version, Dictionary<ParallelStateKey, object> writeSet)
    {
        (int txIndex, int incarnation) = (version.TxIndex, version.Incarnation);
        ConcurrentDictionary<ParallelStateKey, Value> txData = _data[txIndex];
        ref HashSet<ParallelStateKey>? lastWritten = ref _lastWrittenLocations[txIndex];
        lastWritten ??= [];

        bool writeSetChanged = false;

        // ConcurrentDictionary writes are individually atomic; readers (TryRead on higher
        // txs) see either the old or the new Value, never a torn record. The lock here
        // protects the lastWritten HashSet only — ConvertWritesToEstimates may iterate it
        // from another worker concurrently with this tx's owner re-recording.
        lock (lastWritten)
        {
            foreach (KeyValuePair<ParallelStateKey, object> write in writeSet)
            {
                txData[write.Key] = new Value(incarnation, write.Value);
            }

            if (lastWritten.Count != 0)
            {
                using ArrayPoolListRef<ParallelStateKey> toRemove = new(lastWritten.Count);
                foreach (ParallelStateKey id in lastWritten)
                {
                    if (!writeSet.ContainsKey(id))
                    {
                        toRemove.Add(id);
                    }
                }

                if (toRemove.Count > 0)
                {
                    writeSetChanged = true;
                    foreach (ParallelStateKey id in toRemove)
                    {
                        lastWritten.Remove(id);
                        txData.TryRemove(id, out _);
                        RemoveFromKeyWriters(id, txIndex);
                    }
                }
            }

            foreach (ParallelStateKey key in writeSet.Keys)
            {
                if (lastWritten.Add(key))
                {
                    AddToKeyWriters(key, txIndex);
                }
                writeSetChanged = true;
            }
        }

        return writeSetChanged;
    }

    private void AddToKeyWriters(ParallelStateKey key, int txIndex) =>
        _keyWriters.GetOrAdd(key, static _ => new WriterList()).Add(txIndex);

    private void RemoveFromKeyWriters(ParallelStateKey key, int txIndex)
    {
        if (_keyWriters.TryGetValue(key, out WriterList? writers))
        {
            writers.Remove(txIndex);
            // Leave empty WriterList in the map: shrinking races with concurrent readers and
            // the per-key wrapper is tiny. The empty list is cheap to consult.
        }
    }

    /// <summary>
    /// Final write-set of the latest incarnation. Caller must only access after the parallel
    /// runner has drained — no lock is taken.
    /// </summary>
    public ConcurrentDictionary<ParallelStateKey, Value> GetFinalWriteSet(int txIndex) => _data[txIndex];

    /// <summary>
    /// Marks every location previously written by this tx as <see cref="Value.Estimate"/>
    /// so higher txs that read them will register a dependency on the next re-execution.
    /// </summary>
    public void ConvertWritesToEstimates(int txIndex)
    {
        HashSet<ParallelStateKey>? previousLocations = _lastWrittenLocations[txIndex];
        if (previousLocations is null) return;

        ConcurrentDictionary<ParallelStateKey, Value> txData = _data[txIndex];
        // Snapshot under the per-tx lock so we don't race a concurrent ApplyWriteSet
        // mutating the HashSet. The dict writes are lock-free; each [key]=Estimate is
        // an atomic CAS-style replace.
        ParallelStateKey[] snapshot;
        lock (previousLocations)
        {
            if (previousLocations.Count == 0) return;
            snapshot = [.. previousLocations];
        }
        foreach (ParallelStateKey location in snapshot)
        {
            txData[location] = Value.Estimate;
        }
    }

    /// <summary>
    /// Reads <paramref name="location"/> from the perspective of <paramref name="txIndex"/>
    /// via the per-key writer index: jumps directly to the highest writer tx whose index is
    /// below <paramref name="txIndex"/> instead of scanning all lower slots.
    /// </summary>
    /// <returns>
    /// <see cref="Status.Ok"/> with the value and writing-tx version; or
    /// <see cref="Status.NotFound"/> if no lower tx wrote (caller falls back to DB or system
    /// overlay); or <see cref="Status.ReadError"/> if a lower tx wrote an Estimate (caller
    /// must abort and take a dependency).
    /// </returns>
    /// <remarks>
    /// Race handling: the writer index is updated outside the per-tx data dict's atomic
    /// slot. If a writer removes its own write between our index hit and the dict fetch,
    /// the dict lookup misses; we retry with the next-lower writer until exhausted.
    /// </remarks>
    public Status TryRead(ParallelStateKey location, int txIndex, out TxVersion version, out object? value)
    {
        if (_keyWriters.TryGetValue(location, out WriterList? writers))
        {
            int upper = txIndex;
            while (true)
            {
                int writerTx = writers.FindHighestBelow(upper);
                if (writerTx < 0) break;
                if (_data[writerTx].TryGetValue(location, out Value v))
                {
                    version = new TxVersion(writerTx, v.Incarnation);
                    if (v.IsEstimate)
                    {
                        value = null;
                        return Status.ReadError;
                    }
                    value = v.Data;
                    return Status.Ok;
                }
                upper = writerTx;
            }
        }

        if (_systemOverlay is not null && _systemOverlay.TryGetValue(location, out object? overlayValue))
        {
            version = TxVersion.Empty;
            value = overlayValue;
            return Status.Ok;
        }

        version = TxVersion.Empty;
        value = null;
        return Status.NotFound;
    }

    /// <summary>
    /// Re-reads each location in <paramref name="txIndex"/>'s recorded read-set and returns
    /// false if any read is now stale (lower tx wrote with a different version, or the
    /// location was previously present but is no longer, or a lower tx is now an Estimate).
    /// </summary>
    public bool ValidateReadSet(int txIndex)
    {
        HashSet<Read>? priorReads = _lastReads[txIndex];
        if (priorReads is null) return true;

        foreach (Read read in priorReads)
        {
            Status status = TryRead(read.Location, txIndex, out TxVersion version, out _);
            switch (status)
            {
                case Status.Ok when read.TxVersion != version:
                case Status.NotFound when !read.TxVersion.IsEmpty:
                case Status.ReadError:
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Sorted-descending list of tx indices that currently write a given key. Reads are
    /// hot (every TryRead consults one); mutations happen only inside ApplyWriteSet.
    /// </summary>
    /// <remarks>
    /// Uses a plain <see cref="List{T}"/> + monitor lock — for the common case of 1-3
    /// writers per key, this beats a balanced tree on both memory and constant factors.
    /// FindHighestBelow does a manual scan from the front since the list is sorted
    /// descending and typically tiny; a binary search would be a wash at these sizes.
    /// </remarks>
    private sealed class WriterList
    {
        private readonly List<int> _indicesDesc = [];

        public void Add(int txIndex)
        {
            lock (_indicesDesc)
            {
                int idx = _indicesDesc.BinarySearch(txIndex, DescendingComparer.Instance);
                if (idx < 0) _indicesDesc.Insert(~idx, txIndex);
            }
        }

        public void Remove(int txIndex)
        {
            lock (_indicesDesc)
            {
                int idx = _indicesDesc.BinarySearch(txIndex, DescendingComparer.Instance);
                if (idx >= 0) _indicesDesc.RemoveAt(idx);
            }
        }

        public int FindHighestBelow(int txIndex)
        {
            lock (_indicesDesc)
            {
                foreach (int i in _indicesDesc)
                {
                    if (i < txIndex) return i;
                }
                return -1;
            }
        }

        private sealed class DescendingComparer : IComparer<int>
        {
            public static readonly DescendingComparer Instance = new();
            public int Compare(int x, int y) => y.CompareTo(x);
        }
    }
}

/// <summary>
/// A read recorded by a tx: the location it observed, and the version of the writing tx
/// (or <see cref="TxVersion.Empty"/> if the read fell through to the underlying DB).
/// </summary>
public readonly record struct Read(ParallelStateKey Location, TxVersion TxVersion);

public enum Status
{
    /// <summary>Read succeeded; <c>value</c> holds the data.</summary>
    Ok,
    /// <summary>No lower tx wrote this location; caller must read from the base DB.</summary>
    NotFound,
    /// <summary>A lower tx is an Estimate; caller must abort and add a dependency.</summary>
    ReadError
}
