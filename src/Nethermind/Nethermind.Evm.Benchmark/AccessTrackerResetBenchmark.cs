// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Isolates the per-transaction EIP-2929 warm/cold reset primitive: rent a
/// <see cref="StackAccessTracker"/> from the pool, warm a number of distinct
/// addresses and storage cells, then dispose (which clears every set and
/// returns the tracker to the pool) — exactly what happens once per transaction
/// in <c>TransactionProcessor.ExecuteCore</c>.
/// </summary>
/// <remarks>
/// Two costs are measured separately:
/// <list type="bullet">
/// <item><see cref="WarmAllThenReset"/> — a tx warming <see cref="WarmedEntries"/>
/// entries: insert cost + O(warm-set) clear on dispose.</item>
/// <item><see cref="WarmFewThenReset_AfterHighWaterMark"/> — a small tx (8 cells,
/// 2 addresses) reusing a pooled tracker whose <see cref="System.Collections.Generic.HashSet{T}"/>
/// capacity was inflated to <see cref="WarmedEntries"/> by an earlier large tx.
/// <c>HashSet.Clear</c> zeroes the full bucket array, so the clear cost is
/// O(high-water capacity), not O(entries warmed by this tx).</item>
/// </list>
/// </remarks>
[Config(typeof(ResetConfig))]
[MemoryDiagnoser]
[JsonExporterAttribute.FullCompressed]
public class AccessTrackerResetBenchmark
{
    private class ResetConfig : ManualConfig
    {
        public ResetConfig()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessNoEmitToolchain.Instance)
                .WithLaunchCount(1)
                .WithWarmupCount(3)
                .WithIterationCount(10));
            AddColumn(StatisticColumn.Min);
            AddColumn(StatisticColumn.Max);
            AddColumn(StatisticColumn.Median);
        }
    }

    private const int OpsPerInvoke = 100;
    private const int SmallTxCells = 8;
    private const int SmallTxAddresses = 2;
    private const int MaxEntries = 4096;

    [Params(64, 512, 4096)]
    public int WarmedEntries { get; set; }

    private Address[] _addresses = null!;
    private StorageCell[] _cells = null!;

    private void SharedSetup()
    {
        _addresses = new Address[MaxEntries / 16 + SmallTxAddresses];
        for (int i = 0; i < _addresses.Length; i++)
        {
            byte[] bytes = new byte[Address.Size];
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(Address.Size - sizeof(int)), i + 1);
            _addresses[i] = new Address(bytes);
        }

        // Distinct cells spread over a handful of addresses, mimicking a tx touching
        // many slots of a few contracts.
        _cells = new StorageCell[MaxEntries];
        for (int i = 0; i < _cells.Length; i++)
        {
            UInt256 index = (UInt256)i;
            _cells[i] = new StorageCell(_addresses[i & 15], in index);
        }

        DrainTrackerPool();
    }

    [GlobalSetup(Target = nameof(WarmAllThenReset))]
    public void SetupWarmAll() => SharedSetup();

    [GlobalSetup(Target = nameof(WarmFewThenReset_AfterHighWaterMark))]
    public void SetupWarmFew()
    {
        SharedSetup();
        // Inflate the single pooled TrackingState's capacity to WarmedEntries,
        // simulating one large tx earlier in the block.
        using StackAccessTracker tracker = new();
        Warm(in tracker, WarmedEntries, WarmedEntries / 16);
    }

    /// <summary>
    /// Removes instances left in the static tracker pool by other benchmarks in the
    /// same process, so each case starts from freshly-sized sets.
    /// </summary>
    private static void DrainTrackerPool()
    {
        // Renting without disposing drops the pooled TrackingState instances.
        for (int i = 0; i < 64; i++)
        {
            _ = new StackAccessTracker();
        }
    }

    private void Warm(in StackAccessTracker tracker, int cells, int addresses)
    {
        for (int i = 0; i < addresses; i++)
        {
            tracker.WarmUp(_addresses[i]);
        }

        for (int i = 0; i < cells; i++)
        {
            tracker.WarmUp(in _cells[i]);
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = OpsPerInvoke)]
    public void WarmAllThenReset()
    {
        for (int op = 0; op < OpsPerInvoke; op++)
        {
            using StackAccessTracker tracker = new();
            Warm(in tracker, WarmedEntries, Math.Max(SmallTxAddresses, WarmedEntries / 16));
        }
    }

    [Benchmark(OperationsPerInvoke = OpsPerInvoke)]
    public void WarmFewThenReset_AfterHighWaterMark()
    {
        for (int op = 0; op < OpsPerInvoke; op++)
        {
            using StackAccessTracker tracker = new();
            Warm(in tracker, SmallTxCells, SmallTxAddresses);
        }
    }
}
