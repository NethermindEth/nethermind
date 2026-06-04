// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

    // Per-tx write-sets. Each entry is a Dictionary guarded by a RWLock so the executing
    // worker can publish writes (under WriteLock) while higher txs read concurrently
    // (under ReadLock).
    private readonly DataDictionary[] _data = Enumerable.Range(0, txCount)
        .Select(_ => new DataDictionary())
        .ToArray();

    // Locations written by the latest published incarnation of each tx — used to detect
    // removed keys on re-execution and to convert writes to estimates on validation abort.
    private readonly HashSet<ParallelStateKey>?[] _lastWrittenLocations = new HashSet<ParallelStateKey>?[txCount];

    // Reads recorded by the latest published incarnation of each tx — used by
    // ValidateReadSet to detect stale dependencies.
    private readonly HashSet<Read>?[] _lastReads = new HashSet<Read>?[txCount];

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
        DataDictionary txData = _data[txIndex];
        ref HashSet<ParallelStateKey>? lastWritten = ref _lastWrittenLocations[txIndex];
        lastWritten ??= [];

        bool writeSetChanged = false;

        txData.Lock.EnterWriteLock();
        try
        {
            foreach (KeyValuePair<ParallelStateKey, object> write in writeSet)
            {
                txData.Dictionary[write.Key] = new Value(incarnation, write.Value);
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
                        txData.Dictionary.Remove(id, out _);
                    }
                }
            }

            // Mutate lastWritten inside the write lock so concurrent enumerators (e.g. a
            // ConvertWritesToEstimates that beats the status fence) don't see a torn HashSet.
            foreach (ParallelStateKey key in writeSet.Keys)
            {
                lastWritten.Add(key);
                writeSetChanged = true;
            }
        }
        finally
        {
            txData.Lock.ExitWriteLock();
        }

        return writeSetChanged;
    }

    /// <summary>
    /// Final write-set of the latest incarnation. Caller must only access after the parallel
    /// runner has drained — no lock is taken.
    /// </summary>
    public Dictionary<ParallelStateKey, Value> GetFinalWriteSet(int txIndex) => _data[txIndex].Dictionary;

    /// <summary>
    /// Marks every location previously written by this tx as <see cref="Value.Estimate"/>
    /// so higher txs that read them will register a dependency on the next re-execution.
    /// </summary>
    public void ConvertWritesToEstimates(int txIndex)
    {
        HashSet<ParallelStateKey>? previousLocations = _lastWrittenLocations[txIndex];
        if (previousLocations is null) return;

        DataDictionary txData = _data[txIndex];
        txData.Lock.EnterWriteLock();
        try
        {
            foreach (ParallelStateKey location in previousLocations)
            {
                txData.Dictionary[location] = Value.Estimate;
            }
        }
        finally
        {
            txData.Lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Reads <paramref name="location"/> from the perspective of <paramref name="txIndex"/>:
    /// walks lower txs from txIndex-1 downward, returning the first match.
    /// </summary>
    /// <returns>
    /// <see cref="Status.Ok"/> with the value and writing-tx version; or
    /// <see cref="Status.NotFound"/> if no lower tx wrote (caller falls back to DB); or
    /// <see cref="Status.ReadError"/> if a lower tx wrote an Estimate (caller must abort and
    /// take a dependency).
    /// </returns>
    public Status TryRead(ParallelStateKey location, int txIndex, out TxVersion version, out object? value)
    {
        for (int prevTx = txIndex - 1; prevTx >= 0; prevTx--)
        {
            DataDictionary prevTransactionData = _data[prevTx];
            prevTransactionData.Lock.EnterReadLock();
            try
            {
                if (prevTransactionData.Dictionary.TryGetValue(location, out Value v))
                {
                    version = new TxVersion(prevTx, v.Incarnation);
                    if (v.IsEstimate)
                    {
                        value = null;
                        return Status.ReadError;
                    }
                    value = v.Data;
                    return Status.Ok;
                }
            }
            finally
            {
                prevTransactionData.Lock.ExitReadLock();
            }
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

    private sealed class DataDictionary
    {
        public readonly Dictionary<ParallelStateKey, Value> Dictionary = [];
        public readonly ReaderWriterLockSlim Lock = new();
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
