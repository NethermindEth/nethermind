// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

public class MultiVersionMemory<TLogger>(ushort txCount, ParallelTrace<TLogger> parallelTrace) where TLogger : struct, IIsTracing
{
    private readonly record struct Value(ushort Incarnation, byte[] Bytes);
    private static readonly Value Estimate = new(ushort.MaxValue, []);

    private readonly ConcurrentDictionary<int, Value>[] _data =
        Enumerable.Range(0, txCount)
        .Select(_ => new ConcurrentDictionary<int, Value>())
        .ToArray();

    private readonly HashSet<int>?[] _lastWrittenLocations = new HashSet<int>?[txCount];
    private readonly HashSet<Read>?[] _lastReads = new HashSet<Read>[txCount];

    private void ApplyWriteSet(Version version, Dictionary<int, byte[]> writeSet)
    {
        ConcurrentDictionary<int, Value> txData = _data[version.TxIndex];
        foreach (KeyValuePair<int, byte[]> write in writeSet)
        {
            txData[write.Key] = new(version.Incarnation, write.Value);
        }
    }

    private bool UpdateWrittenLocations(int txIndex, Dictionary<int, byte[]> writeSet)
    {
        ConcurrentDictionary<int, Value> txData = _data[txIndex];
        ref HashSet<int>? lastWritten = ref _lastWrittenLocations[txIndex];
        lastWritten ??= new HashSet<int>();

        if (lastWritten.Count != 0)
        {
            using ArrayPoolList<int> toRemove = new(lastWritten.Count);
            foreach (var id in lastWritten)
            {
                if (!writeSet.ContainsKey(id))
                {
                    toRemove.Add(id);
                }
            }

            foreach (var id in toRemove)
            {
                lastWritten.Remove(id);
                txData.TryRemove(id, out _);
            }
        }

        int oldCount = lastWritten.Count;
        lastWritten.UnionWith(writeSet.Keys);
        return lastWritten.Count > oldCount;
    }

    public bool Record(Version version, HashSet<Read> readSet, Dictionary<int, byte[]> writeSet)
    {
        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"{version} Record read-set: {{{string.Join(",", readSet.Select(r => $"{r.Location}:{r.Version}"))}}}, write-set: {{{string.Join(",", writeSet.Select(r => $"{r.Key}:{r.Value.ToHexString()}"))}}}.");
        ApplyWriteSet(version, writeSet);
        bool wroteNewLocation = UpdateWrittenLocations(version.TxIndex, writeSet);
        _lastReads[version.TxIndex] = readSet;
        return wroteNewLocation;
    }

    public void ConvertWritesToEstimates(ushort txIndex)
    {
        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Tx {txIndex} ConvertWritesToEstimates.");
        ConcurrentDictionary<int, Value> txData = _data[txIndex];
        HashSet<int>? previousLocations = _lastWrittenLocations[txIndex];
        if (previousLocations is not null)
        {
            foreach (int location in previousLocations)
            {
                txData[location] = Estimate;
            }
        }
    }

    public Status TryRead(int location, ushort txIndex, out Version version, out byte[]? value)
    {
        long id = parallelTrace.ReserveId();
        ushort prevTx = txIndex;
        prevTx--;
        while (prevTx != ushort.MaxValue)
        {
            ConcurrentDictionary<int, Value> prevTransactionData = _data[prevTx];
            if (prevTransactionData.TryGetValue(location, out Value v))
            {
                version = new Version(prevTx, v.Incarnation);
                if (v == Estimate)
                {
                    value = null;
                    if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add(id, $"Tx {txIndex} TryRead at location {location} returned {Status.ReadError} with blocking {version}.");
                    return Status.ReadError;
                }
                else
                {
                    value = v.Bytes;
                    if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add(id, $"Tx {txIndex} TryRead at location {location} returned {Status.Ok} with value {value.ToHexString()} from {version}.");
                    return Status.Ok;
                }
            }

            prevTx--;
        }

        version = Version.Empty;
        value = null;
        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add(id, $"Tx {txIndex} TryRead at location {location} returned {Status.NotFound}.");
        return Status.NotFound;
    }

    public Dictionary<int, byte[]> Snapshot()
    {
        Dictionary<int, byte[]> result = new Dictionary<int, byte[]>();
        for (var index = _data.Length - 1; index >= 0; index--)
        {
            ConcurrentDictionary<int, Value> data = _data[index];
            foreach (KeyValuePair<int, Value> location in data)
            {
                result.TryAdd(location.Key, location.Value.Bytes);
            }
        }

        return result;
    }

    public bool ValidateReadSet(ushort txIndex)
    {
        HashSet<Read> priorReads = _lastReads[txIndex];
        if (priorReads is not null)
        {
            foreach (Read read in priorReads)
            {
                Status status = TryRead(read.Location, txIndex, out Version version, out _);
                switch (status)
                {
                    case Status.Ok when read.Version != version:
                    case Status.NotFound when !read.Version.IsEmpty:
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
}

public readonly record struct Read(int Location, Version Version);
public enum Status { Ok, NotFound, ReadError };
