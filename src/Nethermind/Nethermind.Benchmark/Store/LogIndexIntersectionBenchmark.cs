// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Db.LogIndex;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Filters.Topics;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// Intersects the log index postings of an address and a topic over warm RocksDB-backed storage.
/// Both methods wrap each posting enumerator in one pass-through call. <see cref="Sequential"/> hides
/// <see cref="ILogIndexEnumerator.MoveTo"/>, so both sides only step forward; <see cref="Leapfrog"/> forwards it.
/// Keep the default out-of-process toolchain: each method then runs in its own process, so the optimizations
/// profiled for one method do not favour the other.
/// </summary>
[MemoryDiagnoser]
public class LogIndexIntersectionBenchmark
{
    private const int BlockCount = 65_536;

    private static readonly Address DenseAddress = TestItem.AddressA;
    private static readonly Address MediumAddress = TestItem.AddressB;
    private static readonly Address SparseAddress = TestItem.AddressC;
    private static readonly Address OtherAddress = TestItem.AddressD;
    private static readonly Hash256 DenseTopic = TestItem.KeccakA;
    private static readonly Hash256 SparseTopic = TestItem.KeccakB;
    private static readonly Hash256 OtherTopic = TestItem.KeccakC;

    private ChunkedLogIndexStorage _index = null!;
    private PassThroughLogIndexStorage _sequential = null!;
    private PassThroughLogIndexStorage _leapfrog = null!;
    private LogFilter _filter = null!;

    /// <summary>Blocks per merged batch: 256 gives many small chunks per key, half the blocks gives one chunk per half.</summary>
    [Params(256, BlockCount / 2)]
    public int ChunkBlocks { get; set; }

    public enum Postings
    {
        SparseDense,
        MediumDense,
        DenseDense,
        SparseSparse
    }

    [Params(Postings.SparseDense, Postings.MediumDense, Postings.DenseDense, Postings.SparseSparse)]
    public Postings Query { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(42);
        LogEntry[][] logs = new LogEntry[BlockCount][];
        for (int block = 0; block < logs.Length; block++)
            logs[block] = CreateLogs(random);

        _index = ChunkedLogIndexStorage.CreateAsync(BlockCount, ChunkBlocks, block => logs[block]).GetAwaiter().GetResult();
        _sequential = new PassThroughLogIndexStorage(_index.Storage, seekable: false);
        _leapfrog = new PassThroughLogIndexStorage(_index.Storage, seekable: true);
        _filter = Query switch
        {
            Postings.SparseDense => Filter(SparseAddress, new SpecificTopic(DenseTopic), AnyTopic.Instance),
            Postings.MediumDense => Filter(MediumAddress, new SpecificTopic(DenseTopic), AnyTopic.Instance),
            Postings.DenseDense => Filter(DenseAddress, new SpecificTopic(DenseTopic), AnyTopic.Instance),
            _ => Filter(SparseAddress, AnyTopic.Instance, new SpecificTopic(SparseTopic))
        };
    }

    [GlobalCleanup]
    public void Cleanup() => _index.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Benchmark(Baseline = true)]
    public long Sequential() => SumMatches(_sequential);

    [Benchmark]
    public long Leapfrog() => SumMatches(_leapfrog);

    private long SumMatches(ILogIndexStorage storage)
    {
        long sum = 0;
        foreach (long block in storage.EnumerateBlockNumbersFor(_filter, 0, BlockCount - 1))
            sum += block;

        return sum;
    }

    private static LogEntry[] CreateLogs(Random random)
    {
        List<LogEntry> logs = [Build.A.LogEntry.WithAddress(OtherAddress).TestObject];
        if (random.Next(10) != 0) logs.Add(Build.A.LogEntry.WithAddress(DenseAddress).TestObject);
        if (random.Next(10) == 0) logs.Add(Build.A.LogEntry.WithAddress(MediumAddress).TestObject);
        if (random.Next(1_000) == 0) logs.Add(Build.A.LogEntry.WithAddress(SparseAddress).TestObject);
        if (random.Next(20) != 0) logs.Add(Build.A.LogEntry.WithAddress(OtherAddress).WithTopics(DenseTopic).TestObject);
        if (random.Next(100) == 0) logs.Add(Build.A.LogEntry.WithAddress(OtherAddress).WithTopics(OtherTopic, SparseTopic).TestObject);
        return [.. logs];
    }

    private static LogFilter Filter(Address address, TopicExpression topic0, TopicExpression topic1) =>
        new(0, BlockParameter.Earliest, BlockParameter.Latest, new AddressFilter(address), new SequenceTopicsFilter(topic0, topic1));

    private sealed class PassThroughLogIndexStorage(ILogIndexStorage storage, bool seekable) : ILogIndexStorage
    {
        public bool Enabled => storage.Enabled;
        public int? MaxBlockNumber => storage.MaxBlockNumber;
        public int? MinBlockNumber => storage.MinBlockNumber;

        public IEnumerator<int> GetEnumerator(Address address, int from, int to) =>
            Wrap(storage.GetEnumerator(address, from, to));

        public IEnumerator<int> GetEnumerator(int topicIndex, Hash256 topic, int from, int to) =>
            Wrap(storage.GetEnumerator(topicIndex, topic, from, to));

        public LogIndexAggregate Aggregate(IReadOnlyList<BlockReceipts> batch, bool isBackwardSync, LogIndexUpdateStats stats = null) =>
            storage.Aggregate(batch, isBackwardSync, stats);

        public Task AddReceiptsAsync(LogIndexAggregate aggregate, LogIndexUpdateStats stats = null) => storage.AddReceiptsAsync(aggregate, stats);
        public Task RemoveReorgedAsync(BlockReceipts block) => storage.RemoveReorgedAsync(block);
        public string GetDbSize() => storage.GetDbSize();
        public Task StopAsync() => storage.StopAsync();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private IEnumerator<int> Wrap(IEnumerator<int> enumerator) =>
            seekable ? new SeekableEnumerator((ILogIndexEnumerator)enumerator) : new SequentialEnumerator(enumerator);
    }

    private sealed class SequentialEnumerator(IEnumerator<int> enumerator) : IEnumerator<int>
    {
        public bool MoveNext() => enumerator.MoveNext();
        public void Reset() => enumerator.Reset();
        public int Current => enumerator.Current;
        object IEnumerator.Current => Current;
        public void Dispose() => enumerator.Dispose();
    }

    private sealed class SeekableEnumerator(ILogIndexEnumerator enumerator) : ILogIndexEnumerator
    {
        public bool MoveNext() => enumerator.MoveNext();
        public bool MoveTo(int target) => enumerator.MoveTo(target);
        public void Reset() => enumerator.Reset();
        public int Current => enumerator.Current;
        object IEnumerator.Current => Current;
        public void Dispose() => enumerator.Dispose();
    }
}
