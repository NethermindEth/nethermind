// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

/// <summary>
/// Keeps track of transactions reads and writes.
/// It is also used when further transaction wants to read location that was written by previous transaction.
/// It is also used to validate read set of a transaction to determine if it needs to be re-executed.
/// </summary>
/// <param name="txCount">How many transactions are in block</param>
/// <param name="parallelTrace">Tracing helper</param>
/// <typeparam name="TLocation">Location key type</typeparam>
/// <typeparam name="TLogger">Should log trace</typeparam>
/// <typeparam name="TData">Data type</typeparam>
public class MultiVersionMemory<TLocation, TData, TLogger>(int txCount, ParallelTrace<TLogger> parallelTrace) where TLogger : struct, IIsTracing where TLocation : notnull
{
    /// <summary>
    /// Information about stored value for a given location
    /// </summary>
    /// <param name="Incarnation">Incarnation of the transaction that stored value</param>
    /// <param name="Data">Actual value written by transaction</param>
    private readonly record struct Value(int Incarnation, TData Data)
    {
        /// <summary>
        /// Special case when we know the transaction will be re-executed, so we can mark it's writes as estimates.
        /// </summary>
        public static readonly Value Estimate = new(-1, default);
        public bool IsEstimate => Incarnation == -1;
    }

    /// <summary>
    /// Mapping between TransactionIndex -> Concurrent Dictionary that will store this transaction writes.
    /// Each Concurrent Dictionary maps memory location to a written value.
    /// </summary>
    /// <remarks>
    /// While only one thread should write to each of the dictionary at same point of time, multiple threads could read it.
    /// </remarks>
    private readonly DataDictionary<TLocation, Value>[] _data =
        Enumerable.Range(0, txCount)
        .Select(_ => new DataDictionary<TLocation, Value>())
        .ToArray();
    // TODO: Consider going to regular Dictionary with ReadWriterLock as writes will be rare and single-threaded
    // TODO: Or we could consider to flatten all the dictionaries per transaction to one big ConcurrentDictionary that would keep the highest transaction write per location

    /// <summary>
    /// Mapping between TransactionIndex -> HashSet that will store locations that were written by last incarnation of the transaction.
    /// </summary>
    private readonly HashSet<TLocation>?[] _lastWrittenLocations = new HashSet<TLocation>?[txCount];

    /// <summary>
    /// Mapping between TransactionIndex -> HashSet that will store reads by last incarnation of the transaction.
    /// </summary>
    private readonly HashSet<Read<TLocation>>?[] _lastReads = new HashSet<Read<TLocation>>[txCount];

    // For given transaction incarnation it stores it's writeset into _data and updates _lastWrittenLocations
    private bool ApplyWriteSet(Version version, Dictionary<TLocation, TData> writeSet)
    {
        DataDictionary<TLocation, Value> txData = _data[version.TxIndex]; // writes of current tx (currently from previous incarnation)
        ref HashSet<TLocation>? lastWritten = ref _lastWrittenLocations[version.TxIndex];
        lastWritten ??= new HashSet<TLocation>();

        txData.Lock.EnterWriteLock();
        foreach (KeyValuePair<TLocation, TData> write in writeSet)
        {
            // TODO: We could potentially not overwrite the key if the value is the same - leaving same lower incarnation
            // This could help in ValidateReadSet where we wouldn't have to check actual value
            // The downside is that we need to do a Read and Write to dictionary most of the times
            // But maybe this will be simplified if used Dictionary with ReadWriterLock?
            txData.Dictionary[write.Key] = new(version.Incarnation, write.Value);
        }

        // if previous incarnation written locations
        if (lastWritten.Count != 0)
        {
            // grab all the locations that were written in previous incarnation, but are not written in current incarnation
            using ArrayPoolList<TLocation> toRemove = new(lastWritten.Count);
            foreach (TLocation id in lastWritten)
            {
                if (!writeSet.ContainsKey(id))
                {
                    toRemove.Add(id);
                }
            }

            // remove them from both last written and txData
            foreach (TLocation id in toRemove)
            {
                lastWritten.Remove(id);
                txData.Dictionary.Remove(id, out _);
            }
        }
        txData.Lock.ExitWriteLock();

        int oldCount = lastWritten.Count;

        // add all currently written locations
        lastWritten.UnionWith(writeSet.Keys);

        // check if any new locations were written
        return lastWritten.Count > oldCount;
    }

    /// <summary>
    /// Updates current transaction incarnation reads and writes.
    /// </summary>
    /// <param name="version">Version of transaction execution</param>
    /// <param name="readSet">Reads</param>
    /// <param name="writeSet">Writes</param>
    /// <returns>If any new location was written, that wasn't written by previous incarnation</returns>
    public bool Record(Version version, HashSet<Read<TLocation>> readSet, Dictionary<TLocation, TData> writeSet)
    {
        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"{version} Record read-set: {{{string.Join(",", readSet.Select(r => $"{r.Location}:{r.Version}"))}}}, write-set: {{{string.Join(",", writeSet.Select(r => $"{r.Key}:{parallelTrace.Format(r.Value)}"))}}}.");
        bool wroteNewLocation = ApplyWriteSet(version, writeSet);
        _lastReads[version.TxIndex] = readSet;
        return wroteNewLocation;
    }

    /// <summary>
    /// Converts all transaction writes to estimates.
    /// </summary>
    /// <param name="txIndex"></param>
    /// <remarks>
    /// Estimates are used, when higher transaction reads a location.
    /// It then knows that it needs to add dependency and wait for this transaction to be executed.
    /// </remarks>
    public void ConvertWritesToEstimates(int txIndex)
    {
        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Tx {txIndex} ConvertWritesToEstimates.");
        HashSet<TLocation>? previousLocations = _lastWrittenLocations[txIndex];
        if (previousLocations is not null)
        {
            DataDictionary<TLocation, Value> txData = _data[txIndex];
            txData.Lock.EnterWriteLock();
            foreach (TLocation location in previousLocations)
            {
                txData.Dictionary[location] = Value.Estimate;
            }
            txData.Lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Tries to read the location from memory.
    /// </summary>
    /// <param name="location">Location to read</param>
    /// <param name="txIndex">Index of transaction that is reading</param>
    /// <param name="version">out version that was read - information about transaction that written to this location</param>
    /// <param name="value">Value that was read</param>
    /// <returns>
    /// <see cref="Status.Ok"/> when read succeeds.
    /// <see cref="Status.NotFound"/> when value for location is not in memory and needs to be read from database.
    /// <see cref="Status.ReadError"/> when we know previous transaction needs to be re-executed first to correctly read the location.
    /// </returns>
    public Status TryRead(TLocation location, int txIndex, out Version version, out TData value)
    {
        long id = parallelTrace.ReserveId();
        // start from previous transaction and go back through all the previous transactions
        for (int prevTx = txIndex - 1; prevTx >= 0; prevTx--)
        {
            DataDictionary<TLocation, Value> prevTransactionData = _data[prevTx];
            prevTransactionData.Lock.EnterReadLock();
            try
            {
                // if we find the location written by previous transaction
                if (prevTransactionData.Dictionary.TryGetValue(location, out Value v))
                {
                    version = new Version(prevTx, v.Incarnation); // return version info
                    if (v.IsEstimate)
                    {
                        // if estimate (prevTx needs re-execution) return ReadError.
                        value = default;
                        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add(id, $"Tx {txIndex} TryRead at location {location} returned {Status.ReadError} with blocking {version}.");
                        return Status.ReadError;
                    }
                    else
                    {
                        // else we can return the value
                        value = v.Data;
                        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add(id, $"Tx {txIndex} TryRead at location {location} returned {Status.Ok} with value {parallelTrace.Format(value)} from {version}.");
                        return Status.Ok;
                    }
                }
            }
            finally
            {
                prevTransactionData.Lock.ExitReadLock();
            }
        }

        // we iterated through all transactions and didn't find any
        version = Version.Empty;
        value = default;
        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add(id, $"Tx {txIndex} TryRead at location {location} returned {Status.NotFound}.");
        return Status.NotFound;
    }

    /// <summary>
    /// Grabs the end result write-set of whole block
    /// </summary>
    /// <returns>Write set of the block</returns>
    public Dictionary<TLocation, TData> Snapshot()
    {
        Dictionary<TLocation, TData> result = new();
        // need to iterate backwards, as the later transaction writes are the final writes to the same location
        for (var index = _data.Length - 1; index >= 0; index--)
        {
            DataDictionary<TLocation, Value> data = _data[index];
            foreach (KeyValuePair<TLocation, Value> location in data.Dictionary)
            {
                // only add if previously not added
                result.TryAdd(location.Key, location.Value.Data);
            }
        }

        return result;
    }

    /// <summary>
    /// Validates if transaction reads, recorded in <see cref="Record"/> are dependent on any previous transactions.
    /// </summary>
    /// <param name="txIndex">Transaction index to validate</param>
    /// <returns>true if they are independent and transaction doesn't need re-excecution, false if they are dependent</returns>
    /// <remarks>
    /// Keep in mind that this validation is only against current known state.
    /// If new incarnation of lower-index transaction will write new values, then this validation will be done again.
    /// </remarks>
    public bool ValidateReadSet(int txIndex)
    {
        HashSet<Read<TLocation>> priorReads = _lastReads[txIndex];
        if (priorReads is not null)
        {
            // for each of transaction reads...
            foreach (Read<TLocation> read in priorReads)
            {
                // Try do read again on current state
                Status status = TryRead(read.Location, txIndex, out Version version, out _);
                switch (status)
                {
                    // if we read different version (so later incarnation of dependent transaction wrote to same slot
                    // TODO: We could potentially also check the value, if the value is the same we can consider it valid? Or not lover incarnation when applying the set
                    case Status.Ok when read.Version != version:
                    // if currently we don't find the location, but read the version isn't empty
                    // this means that location was written previously by some lower transaction, but re-execution removed this write
                    case Status.NotFound when !read.Version.IsEmpty:
                    // Read error, we know previous transaction that written to this location will be re-executed, so we cannot be certain about validity of this tx reads
                    case Status.ReadError:
                        {
                            if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Tx {txIndex} ValidateReadSet failed.");
                            return false;
                        }
                }
            }
        }

        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Tx {txIndex} ValidateReadSet succeeded.");
        return true;
    }

    // public ISingleBlockProcessingCache<Address, byte[]> GetAddressCache(ISingleBlockProcessingCache<Address, byte[]> innerCache, ushort txIndex, ushort incarnation)
    // {
    //     return new ExecutionCache<Address, byte[]>(this, innerCache, txIndex, incarnation);
    // }

    // public class ExecutionCache<TKey, TValue>(
    //     MultiVersionMemory multiVersionMemory,
    //     ISingleBlockProcessingCache<Address, byte[]> innerCache,
    //     ushort txIndex,
    //     ushort incarnation) : ISingleBlockProcessingCache<TKey, TValue>
    // {
    //     public TValue? GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
    //     {
    //     }
    //
    //     public bool TryGetValue(TKey key, out TValue value)
    //     {
    //         throw new NotImplementedException();
    //     }
    //
    //     public TValue this[TKey key]
    //     {
    //         get => throw new NotImplementedException();
    //         set => throw new NotImplementedException();
    //     }
    //
    //     public bool NoResizeClear()
    //     {
    //         throw new NotImplementedException();
    //     }
    // }

    private readonly struct DataDictionary<TKey, TValue>
    {
        public readonly Dictionary<TKey, TValue> Dictionary = new();
        public readonly ReaderWriterLockSlim Lock = new();

        public DataDictionary()
        {
        }
    }
}

/// <summary>
/// Information about Read Location
/// </summary>
/// <param name="Location">Location that was read</param>
/// <param name="Version">Version of transaction that written that Location earlier. <see cref="ParallelProcessing.Version.Empty"/> if Location was read from database</param>
/// <typeparam name="TLocation">Location Id type</typeparam>
public readonly record struct Read<TLocation>(TLocation Location, Version Version); // TODO: version->incarnation?

/// <summary>
/// Information about status of reading a Location
/// </summary>
public enum Status
{
    /// <summary>
    /// Location was successfully read from memory
    /// </summary>
    Ok,

    /// <summary>
    /// Location wasn't found in memory, needs to be read from database
    /// </summary>
    NotFound,

    /// <summary>
    /// Location was read as estimate.
    /// This indicates need to add dependency on transaction that has estimate and re-execute later.
    /// </summary>
    ReadError
}
