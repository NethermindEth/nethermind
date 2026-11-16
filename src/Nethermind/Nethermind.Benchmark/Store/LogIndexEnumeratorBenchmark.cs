// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// Walks the log index postings of one address over warm RocksDB-backed storage, forward and backward.
/// Small chunks give many stored values per key, so the per-value decoding cost dominates.
/// </summary>
[MemoryDiagnoser]
public class LogIndexEnumeratorBenchmark
{
    private const int BlockCount = 65_536;

    private static readonly Address DenseAddress = TestItem.AddressA;
    private static readonly Address MediumAddress = TestItem.AddressB;
    private static readonly Address OtherAddress = TestItem.AddressC;

    private ChunkedLogIndexStorage _index;
    private Address _address;

    /// <summary>Blocks per merged batch: 256 gives many small chunks per key, 4096 fewer and larger ones.</summary>
    [Params(256, 4_096)]
    public int ChunkBlocks { get; set; }

    /// <summary>Whether the address logs in about nine of ten blocks or in one of ten.</summary>
    [Params(true, false)]
    public bool Dense { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(42);
        LogEntry[][] logs = new LogEntry[BlockCount][];
        for (int block = 0; block < logs.Length; block++)
            logs[block] = CreateLogs(random);

        _index = ChunkedLogIndexStorage.CreateAsync(BlockCount, ChunkBlocks, block => logs[block]).GetAwaiter().GetResult();
        _address = Dense ? DenseAddress : MediumAddress;
    }

    [GlobalCleanup]
    public void Cleanup() => _index.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Benchmark]
    public long Forward()
    {
        using IEnumerator<int> enumerator = _index.Storage.GetEnumerator(_address, 0, BlockCount - 1);
        return Sum(enumerator);
    }

    [Benchmark]
    public long Reverse()
    {
        using IEnumerator<int> enumerator = _index.Storage.TryGetReverseEnumerator(_address, 0, BlockCount - 1)
            ?? throw new InvalidOperationException("The database has no reverse views.");
        return Sum(enumerator);
    }

    private static long Sum(IEnumerator<int> enumerator)
    {
        long sum = 0;
        while (enumerator.MoveNext())
            sum += enumerator.Current;

        return sum;
    }

    private static LogEntry[] CreateLogs(Random random)
    {
        List<LogEntry> logs = [Build.A.LogEntry.WithAddress(OtherAddress).TestObject];
        if (random.Next(10) != 0) logs.Add(Build.A.LogEntry.WithAddress(DenseAddress).TestObject);
        if (random.Next(10) == 0) logs.Add(Build.A.LogEntry.WithAddress(MediumAddress).TestObject);
        return [.. logs];
    }
}
