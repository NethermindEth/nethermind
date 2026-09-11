// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Int256;

namespace Nethermind.Benchmarks.Core;

/// <summary>Compares materialized storage reads with worker-owned bitmap coverage.</summary>
/// <remarks>
/// Each invocation covers one block with 4,096 declared reads across 16 accounts. Each worker
/// traverses the whole declaration in sparse or dense slices, repeating it to measure reuse.
/// Workers run serially to isolate CPU and allocation costs from scheduling. Both paths include
/// account recording, slice reset, block aggregation and coverage checking; bitmap setup and
/// disposal are measured. This does not measure EVM execution, database reads or parallel speedup.
/// </remarks>
[MemoryDiagnoser]
public class BalReadCoverageBenchmarks
{
    private const int ReadCount = 4096;
    private const int AccountCount = 16;
    private StorageCell[] _reads = null!;
    private ReadOnlyBlockAccessList _declared = null!;

    [Params(1, 8)]
    public int WorkerCount { get; set; }

    [Params(16, ReadCount)]
    public int ReadsPerSlice { get; set; }

    [Params(1, 4)]
    public int Passes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _reads = new StorageCell[ReadCount];
        ReadOnlyAccountChanges[] accounts = new ReadOnlyAccountChanges[AccountCount];
        for (int account = 0; account < AccountCount; account++)
        {
            byte[] bytes = new byte[Address.Size];
            bytes[^1] = (byte)(account + 1);
            Address address = new(bytes);
            UInt256[] slots = new UInt256[ReadCount / AccountCount];
            for (int slot = 0; slot < slots.Length; slot++)
            {
                slots[slot] = (UInt256)slot;
                // Interleave accounts so sparse slices touch distant bitmap words.
                _reads[slot * AccountCount + account] = new StorageCell(address, slots[slot]);
            }
            accounts[account] = new(address, [], slots, [], [], []);
        }
        _declared = new(accounts, AccountCount + ReadCount);
        ulong expected = (ulong)(ReadCount * WorkerCount * Passes);
        if (Materialized() != expected || Bitmap() != expected)
            throw new InvalidOperationException("Read coverage paths disagree with the workload.");
    }

    [Benchmark(Baseline = true)]
    public ulong Materialized() => Execute(useBitmap: false);

    [Benchmark]
    public ulong Bitmap() => Execute(useBitmap: true);

    private ulong Execute(bool useBitmap)
    {
        using BalReadStoragePlan? plan = useBitmap ? new(_declared) : null;
        GeneratedBlockAccessList block = new();
        ulong chargeable = 0;
        for (int worker = 0; worker < WorkerCount; worker++)
        {
            BalReadCoverage? coverage = plan?.CreateCoverage();
            BlockAccessListAtIndex slice = new();
            for (int pass = 0; pass < Passes; pass++)
            {
                for (int start = 0; start < ReadCount; start += ReadsPerSlice)
                {
                    slice.Clear();
                    coverage?.StartSlice();
                    // Repeat the slice to exercise deduplication beyond the last-cell cache.
                    for (int repeat = 0; repeat < 2; repeat++)
                    {
                        for (int i = start; i < start + ReadsPerSlice; i++)
                        {
                            ref readonly StorageCell read = ref _reads[i];
                            if (coverage?.TryMark(read) == true)
                                slice.RecordReadAndGet(read.Address);
                            else
                                slice.RecordStorageReadAndGet(read.Address, read.Index);
                        }
                    }
                    if (coverage is not null)
                        chargeable += coverage.ChargeableReadCount;
                    else
                        foreach (AccountChangesAtIndex account in slice.AccountChanges)
                            chargeable += (ulong)account.StorageReads.Count;
                    block.Merge(slice);
                }
            }
        }
        if (plan is not null)
        {
            if (plan.TryFindUncovered(out _)) throw new InvalidOperationException("Uncovered bitmap read.");
        }
        else
        {
            foreach (ReadOnlyAccountChanges account in _declared.AccountChanges)
            {
                GeneratedAccountChanges actual = block.GetAccountChanges(account.Address)!;
                using ArrayPoolListRef<UInt256> reads = actual.GetSortedStorageReads();
                if (!reads.AsSpan().SequenceEqual(account.StorageReads))
                    throw new InvalidOperationException("Uncovered materialized read.");
            }
        }
        return chargeable;
    }
}
