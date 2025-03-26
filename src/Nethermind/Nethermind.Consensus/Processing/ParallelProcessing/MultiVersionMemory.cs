// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Nethermind.Core;
using Nethermind.State;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

public class MultiVersionMemory(ushort txCount)
{
    public record struct Version(ushort TxIndex, ushort Incarnation);
    private record struct Value(ushort Incarnation, byte[] Bytes);
    public record struct Read(int Location, Version Version);
    private static readonly Value Estimate = new(ushort.MaxValue, []);
    private static readonly Version Empty = new(ushort.MaxValue, ushort.MaxValue);

    public enum Status { Ok, NotFound, ReadError };

    private readonly ConcurrentDictionary<int, Value>[] _data =
        Enumerable.Range(0, txCount)
        .Select(_ => new ConcurrentDictionary<int, Value>())
        .ToArray();

    private readonly int[][] _lastWrittenLocations = new int[txCount][];
    private readonly Read[][] _lastReads = new Read[txCount][];

    public void ApplyWriteSet(ushort txIndex, ushort incarnation, int[] writeLocations, byte[][] writeValues)
    {
        Debug.Assert(writeLocations.Length == writeValues.Length);
        ConcurrentDictionary<int, Value> txData = _data[txIndex];
        for (int i = 0; i < writeLocations.Length; i++)
        {
            txData[writeLocations[i]] = new(incarnation, writeValues[i]);
        }
    }

    public bool UpdateWrittenLocations(int txIndex, int[] newLocations)
    {
        ConcurrentDictionary<int, Value> txData = _data[txIndex];
        ref int[] previousWrittenLocations = ref _lastWrittenLocations[txIndex];
        foreach (int unwrittenLocation in previousWrittenLocations.Except(newLocations))
        {
            txData.Remove(unwrittenLocation, out _);
        }
        previousWrittenLocations = newLocations;
        return newLocations.Except(previousWrittenLocations).Any();
    }

    public bool Record(ushort txIndex, ushort incarnation, Read[] readSet, int[] writeLocations, byte[][] writeValues)
    {
        ApplyWriteSet(txIndex, incarnation, writeLocations, writeValues);
        bool wroteNewLocation = UpdateWrittenLocations(txIndex, writeLocations);
        _lastReads[txIndex] = readSet;
        return wroteNewLocation;
    }

    public void ConvertWritesToEstimates(ushort txIndex)
    {
        ConcurrentDictionary<int, Value> txData = _data[txIndex];
        int[] previousLocations = _lastWrittenLocations[txIndex];
        foreach (int location in previousLocations)
        {
            txData[location] = Estimate;
        }
    }

    public Status TryRead(int location, ushort txIndex, out Version version, out byte[]? value)
    {
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
                    return Status.ReadError;
                }
                else
                {
                    value = v.Bytes;
                    return Status.Ok;
                }
            }

            prevTx--;
        }

        version = Empty;
        value = null;
        return Status.NotFound;
    }

    public void Snapshot()
    {

    }

    public bool ValidateReadSet(ushort txIndex)
    {
        Read[] priorReads = _lastReads[txIndex];
        foreach (Read read in priorReads)
        {
            Status status = TryRead(read.Location, txIndex, out Version version, out byte[]? value);
            switch (status)
            {
                case Status.Ok when read.Version != version:
                case Status.NotFound when read.Version != Empty:
                case Status.ReadError:
                    return false;
            }
        }

        return true;
    }

    public ISingleBlockProcessingCache<Address, byte[]> GetAddressCache(ISingleBlockProcessingCache<Address, byte[]> innerCache, ushort txIndex, ushort incarnation)
    {
        return new ExecutionCache<Address, byte[]>(this, innerCache, txIndex, incarnation);
    }

    public class ExecutionCache<TKey, TValue>(
        MultiVersionMemory multiVersionMemory,
        ISingleBlockProcessingCache<Address, byte[]> innerCache,
        ushort txIndex,
        ushort incarnation) : ISingleBlockProcessingCache<TKey, TValue>
    {
        public TValue? GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
        {
            multiVersionMemory
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            throw new NotImplementedException();
        }

        public TValue this[TKey key]
        {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }

        public bool NoResizeClear()
        {
            throw new NotImplementedException();
        }
    }
}

