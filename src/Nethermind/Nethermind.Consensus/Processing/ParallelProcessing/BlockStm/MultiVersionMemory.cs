// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;

namespace Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;

/// <summary>
/// Tracks per-transaction reads and writes for the Block-STM scheduler. Used to serve
/// later txs' reads from earlier txs' writes, to flag re-execution dependencies, and to
/// validate a tx's read-set against the current memory state.
/// </summary>
public sealed class MultiVersionMemory
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

    // Per-tx write-sets: lock-free reads on the hot path.
    private readonly ConcurrentDictionary<ParallelStateKey, Value>[] _data;

    // Per-key writer index — lets TryRead jump straight to the relevant writer slot.
    private readonly ConcurrentDictionary<ParallelStateKey, WriterList> _keyWriters = new();

    // Latest published write locations per tx — used to detect removed keys on re-record and drive ConvertWritesToEstimates.
    private readonly HashSet<ParallelStateKey>?[] _lastWrittenLocations;

    // Latest published read-set per tx — used by ValidateReadSet. List<Read> instead of
    // HashSet<Read>: dedup is rare in practice (StateProvider's per-tx account cache + EVM's
    // SLOAD warm-cache already short-circuit duplicates before they reach MVMM), and the
    // enumerator over a span beats a HashSet's bucket walk by ~3x per entry.
    private readonly List<Read>?[] _lastReads;

    public MultiVersionMemory(int txCount)
    {
        _data = new ConcurrentDictionary<ParallelStateKey, Value>[txCount];
        for (int i = 0; i < txCount; i++)
        {
            _data[i] = new ConcurrentDictionary<ParallelStateKey, Value>();
        }
        _lastWrittenLocations = new HashSet<ParallelStateKey>?[txCount];
        _lastReads = new List<Read>?[txCount];
    }

    // EIP-4788 / EIP-2935 writes captured before the parallel run; consulted as the last fallback before NotFound.
    private Dictionary<ParallelStateKey, object>? _systemOverlay;

    /// <summary>Seeds pre-block system-contract writes. Must be called before workers start.</summary>
    public void SeedSystemOverlay(Dictionary<ParallelStateKey, object> writes) => _systemOverlay = writes;

    /// <summary>
    /// Publishes a transaction's read- and write-set. Returns true when higher txs that
    /// already read this tx may need re-validation (any added, removed, or re-written
    /// key — the stored TxVersion advances even when the value is unchanged).
    /// </summary>
    public bool Record(TxVersion version, List<Read> readSet, Dictionary<ParallelStateKey, object> writeSet)
    {
        bool writeSetChanged = ApplyWriteSet(version, writeSet);
        // Ownership transfers to MVMM here: future validations of higher txs iterate this list
        // until a newer Record for the same txIndex overwrites the slot. The scope provider's
        // pool of working lists never touches a list after it lands in _lastReads.
        _lastReads[version.TxIndex] = readSet;
        return writeSetChanged;
    }

    /// <summary>
    /// Iterates each non-null published read-set so the caller can return their backing
    /// storage to a pool. Safe only after the parallel runner has drained — no validators may
    /// be active.
    /// </summary>
    public void DrainReadSets(Action<List<Read>> visit)
    {
        for (int i = 0; i < _lastReads.Length; i++)
        {
            List<Read>? list = _lastReads[i];
            if (list is null) continue;
            _lastReads[i] = null;
            visit(list);
        }
    }

    private bool ApplyWriteSet(TxVersion version, Dictionary<ParallelStateKey, object> writeSet)
    {
        (int txIndex, int incarnation) = (version.TxIndex, version.Incarnation);
        ConcurrentDictionary<ParallelStateKey, Value> txData = _data[txIndex];
        ref HashSet<ParallelStateKey>? lastWritten = ref _lastWrittenLocations[txIndex];
        lastWritten ??= [];

        bool writeSetChanged = false;

        // Lock protects lastWritten only; ConcurrentDictionary writes don't need it.
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
        // Empty WriterList stays in the map; shrinking would race concurrent readers.
        if (_keyWriters.TryGetValue(key, out WriterList? writers)) writers.Remove(txIndex);
    }

    /// <summary>Final write-set of the latest incarnation. Caller must access only after the parallel runner has drained.</summary>
    public ConcurrentDictionary<ParallelStateKey, Value> GetFinalWriteSet(int txIndex) => _data[txIndex];

    /// <summary>Marks every location previously written by this tx as <see cref="Value.Estimate"/> so dependents will re-take a dep on the next read.</summary>
    public void ConvertWritesToEstimates(int txIndex)
    {
        HashSet<ParallelStateKey>? previousLocations = _lastWrittenLocations[txIndex];
        if (previousLocations is null) return;

        ConcurrentDictionary<ParallelStateKey, Value> txData = _data[txIndex];
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

    /// <summary>Reads <paramref name="location"/> from the perspective of <paramref name="txIndex"/> via the per-key writer index.</summary>
    /// <returns><see cref="Status.Ok"/>, <see cref="Status.NotFound"/>, or <see cref="Status.ReadError"/> (Estimate hit — caller must abort).</returns>
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

    /// <summary>Re-reads each location in <paramref name="txIndex"/>'s read-set and returns false if any read is now stale.</summary>
    public bool ValidateReadSet(int txIndex)
    {
        List<Read>? priorReads = _lastReads[txIndex];
        if (priorReads is null) return true;

        // Span iteration: avoids the List<>.Enumerator's bounds-check-per-item overhead and
        // beats HashSet<>'s bucket walk by ~3x per entry. Safe because validation runs after
        // Record (no concurrent mutation of this list).
        foreach (ref readonly Read read in CollectionsMarshal.AsSpan(priorReads))
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

    /// <summary>Sorted-descending list of tx indices that currently write a given key.</summary>
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
