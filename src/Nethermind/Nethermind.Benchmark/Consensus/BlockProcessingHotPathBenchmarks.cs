// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;

namespace Nethermind.Benchmarks.Consensus;

/// <summary>
/// Bloom computation per block: sequential loop vs <see cref="ParallelUnbalancedWork"/>.
/// <see cref="ParallelUnbalancedWork.For{TLocal}(int,int,System.Threading.Tasks.ParallelOptions,TLocal,System.Func{int,TLocal,TLocal})"/>
/// always queues ProcessorCount-1 ThreadPool items regardless of the iteration count, so for
/// small receipt counts the scheduling cost dominates the per-receipt bloom work.
/// </summary>
[MemoryDiagnoser]
[HideColumns("Job", "RatioSD")]
public class BloomCalculationBenchmark
{
    [Params(1, 4, 16, 32, 64, 200)]
    public int ReceiptCount { get; set; }

    private TxReceipt[] _receipts = null!;

    [GlobalSetup]
    public void Setup()
    {
        Hash256[] topics = [Keccak.Compute("a"), Keccak.Compute("b")];
        byte[] data = new byte[32];
        _receipts = new TxReceipt[ReceiptCount];
        for (int i = 0; i < ReceiptCount; i++)
        {
            _receipts[i] = new TxReceipt
            {
                Logs = [new LogEntry(Address.Zero, data, topics), new LogEntry(Address.Zero, data, topics)]
            };
        }
    }

    [Benchmark(Baseline = true)]
    public void Parallel() => ParallelUnbalancedWork.For(
        0,
        _receipts.Length,
        ParallelUnbalancedWork.DefaultOptions,
        _receipts,
        static (i, receipts) =>
        {
            receipts[i].CalculateBloom();
            return receipts;
        });

    [Benchmark]
    public void Sequential()
    {
        for (int i = 0; i < _receipts.Length; i++)
            _receipts[i].CalculateBloom();
    }
}

/// <summary>
/// Sorting the per-block code batch before persisting: LINQ <c>OrderBy</c> (allocates an
/// ordered-enumerable plus an internal buffer) vs an in-place sort over a pooled buffer.
/// </summary>
[MemoryDiagnoser]
[HideColumns("Job", "RatioSD")]
public class CodeBatchSortBenchmark
{
    [Params(0, 1, 8, 64)]
    public int CodeCount { get; set; }

    private Dictionary<Hash256AsKey, byte[]> _dict = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dict = new Dictionary<Hash256AsKey, byte[]>(CodeCount);
        for (int i = 0; i < CodeCount; i++)
            _dict[new Hash256AsKey(Keccak.Compute(i.ToString()))] = new byte[64];
    }

    [Benchmark(Baseline = true)]
    public int OrderByLinq()
    {
        int total = 0;
        foreach (KeyValuePair<Hash256AsKey, byte[]> kvp in _dict.OrderBy(static kvp => kvp.Key))
            total += kvp.Value.Length;
        return total;
    }

    [Benchmark]
    public int PooledSort()
    {
        int total = 0;
        using ArrayPoolListRef<KeyValuePair<Hash256AsKey, byte[]>> entries = _dict.ToPooledListRef();
        entries.Sort(static (a, b) => a.Key.CompareTo(b.Key));
        foreach (KeyValuePair<Hash256AsKey, byte[]> kvp in entries)
            total += kvp.Value.Length;
        return total;
    }
}

/// <summary>
/// Allocation cost of building a LOG entry with no topics and no data. <c>new T[0]</c> always
/// allocates a fresh array, whereas the collection expression <c>[]</c> resolves to the cached
/// <see cref="Array.Empty{T}"/> singleton. <c>ReadOnlyMemory&lt;byte&gt;.ToArray()</c> on an empty
/// buffer already returns the cached empty array, so only the topics array is worth guarding.
/// </summary>
[MemoryDiagnoser]
[HideColumns("Job", "RatioSD")]
public class LogEmptyArrayBenchmark
{
    private ReadOnlyMemory<byte> _emptyData;
    // Runtime-valued length mirrors the production code's `new Hash256[topicsCount]`, where
    // topicsCount is 0 for a LOG0; a literal `new Hash256[0]` would be rewritten by the analyzer.
    private int _topicsCount;

    [GlobalSetup]
    public void Setup()
    {
        _emptyData = ReadOnlyMemory<byte>.Empty;
        _topicsCount = 0;
    }

    [Benchmark(Baseline = true)]
    public (Hash256[], byte[]) NewArray()
    {
        Hash256[] topics = new Hash256[_topicsCount];
        byte[] data = _emptyData.ToArray();
        return (topics, data);
    }

    [Benchmark]
    public (Hash256[], byte[]) EmptySingleton()
    {
        Hash256[] topics = [];
        byte[] data = _emptyData.ToArray();
        return (topics, data);
    }
}
