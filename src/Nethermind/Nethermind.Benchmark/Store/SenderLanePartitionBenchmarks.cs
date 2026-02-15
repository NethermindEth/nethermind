// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// Benchmarks the sender-lane partitioning algorithm used by BlockCachePreWarmer to
/// distribute transactions across parallel lanes while maintaining per-sender ordering.
/// Measures allocation overhead, partitioning throughput, and lane balance.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 6)]
public class SenderLanePartitionBenchmarks
{
    private Address[] _senders = null!;
    private int[] _txSenderIndices = null!;

    [Params(96, 256, 1024)]
    public int TransactionCount { get; set; }

    [Params(16, 64, 256)]
    public int UniqueSenderCount { get; set; }

    [Params(4, 16, 64)]
    public int LaneCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(42);
        byte[] buffer = new byte[Address.Size];

        _senders = new Address[UniqueSenderCount];
        for (int i = 0; i < UniqueSenderCount; i++)
        {
            random.NextBytes(buffer);
            _senders[i] = new Address((byte[])buffer.Clone());
        }

        // Assign each tx a sender (round-robin for predictable distribution)
        _txSenderIndices = new int[TransactionCount];
        for (int i = 0; i < TransactionCount; i++)
        {
            _txSenderIndices[i] = i % UniqueSenderCount;
        }
    }

    [Benchmark(Baseline = true)]
    public int PartitionIntoLanes()
    {
        int laneCount = Math.Min(TransactionCount, LaneCount);
        int[] laneStarts = ArrayPool<int>.Shared.Rent(laneCount + 1);
        int[] txIndices = ArrayPool<int>.Shared.Rent(TransactionCount);
        int[] laneWriteOffsets = ArrayPool<int>.Shared.Rent(laneCount);
        int[] txLanes = ArrayPool<int>.Shared.Rent(TransactionCount);

        try
        {
            Array.Clear(laneWriteOffsets, 0, laneCount);

            for (int i = 0; i < TransactionCount; i++)
            {
                Address sender = _senders[_txSenderIndices[i]];
                int laneIndex = GetSenderLane(sender, laneCount);
                txLanes[i] = laneIndex;
                laneWriteOffsets[laneIndex]++;
            }

            int nextOffset = 0;
            for (int laneIdx = 0; laneIdx < laneCount; laneIdx++)
            {
                laneStarts[laneIdx] = nextOffset;
                int laneSize = laneWriteOffsets[laneIdx];
                laneWriteOffsets[laneIdx] = nextOffset;
                nextOffset += laneSize;
            }

            laneStarts[laneCount] = nextOffset;

            for (int i = 0; i < TransactionCount; i++)
            {
                int laneIndex = txLanes[i];
                txIndices[laneWriteOffsets[laneIndex]++] = i;
            }

            // Return the max lane size as a sink value to prevent dead code elimination
            int maxLaneSize = 0;
            for (int laneIdx = 0; laneIdx < laneCount; laneIdx++)
            {
                int laneSize = laneStarts[laneIdx + 1] - laneStarts[laneIdx];
                if (laneSize > maxLaneSize) maxLaneSize = laneSize;
            }

            return maxLaneSize;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(laneStarts, clearArray: false);
            ArrayPool<int>.Shared.Return(txIndices, clearArray: false);
            ArrayPool<int>.Shared.Return(laneWriteOffsets, clearArray: false);
            ArrayPool<int>.Shared.Return(txLanes, clearArray: false);
        }
    }

    [Benchmark]
    public int PartitionIntoLanes_WithBalanceCheck()
    {
        int laneCount = Math.Min(TransactionCount, LaneCount);
        int[] laneCounts = ArrayPool<int>.Shared.Rent(laneCount);

        try
        {
            Array.Clear(laneCounts, 0, laneCount);

            for (int i = 0; i < TransactionCount; i++)
            {
                Address sender = _senders[_txSenderIndices[i]];
                int laneIndex = GetSenderLane(sender, laneCount);
                laneCounts[laneIndex]++;
            }

            // Compute imbalance metric: max/avg ratio
            int sum = 0;
            int max = 0;
            for (int i = 0; i < laneCount; i++)
            {
                sum += laneCounts[i];
                if (laneCounts[i] > max) max = laneCounts[i];
            }

            return max;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(laneCounts, clearArray: false);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetSenderLane(Address sender, int laneCount)
    {
        return (int)((uint)sender.GetHashCode() % (uint)laneCount);
    }
}
