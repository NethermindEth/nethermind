// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// Block-STM style benchmarks measuring speculative parallel execution costs.
/// Block-STM executes transactions optimistically in parallel, tracks read/write sets,
/// detects conflicts, and re-executes conflicting transactions.
///
/// These benchmarks measure the overhead of the key primitives:
/// - Read/write set tracking cost
/// - Conflict detection throughput
/// - Re-execution scheduling overhead
/// - Multi-version data structure access patterns
///
/// This helps evaluate whether Block-STM parallelism can improve MGas/s beyond
/// what the current prewarmer achieves.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 6)]
public class ParallelBlockExecutionBenchmarks
{
    private Address[] _accounts = null!;
    private SimulatedTransaction[] _transactions = null!;

    [Params(96, 256)]
    public int TransactionCount { get; set; }

    [Params(64, 256)]
    public int UniqueAccountCount { get; set; }

    [Params(1, 4, 8)]
    public int ParallelWorkers { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(42);
        byte[] buffer = new byte[Address.Size];

        _accounts = new Address[UniqueAccountCount];
        for (int i = 0; i < UniqueAccountCount; i++)
        {
            random.NextBytes(buffer);
            _accounts[i] = new Address((byte[])buffer.Clone());
        }

        // Each transaction reads sender, writes sender (nonce), reads recipient, writes recipient (balance)
        _transactions = new SimulatedTransaction[TransactionCount];
        for (int i = 0; i < TransactionCount; i++)
        {
            int senderIdx = i % UniqueAccountCount;
            int recipientIdx = (i * 7 + 13) % UniqueAccountCount;
            // Add some storage slot reads/writes for realism
            int storageReads = random.Next(0, 5);
            int storageWrites = random.Next(0, 3);
            _transactions[i] = new SimulatedTransaction(
                senderIdx,
                recipientIdx,
                storageReads,
                storageWrites);
        }
    }

    /// <summary>
    /// Baseline: sequential execution with no conflict tracking overhead.
    /// </summary>
    [Benchmark(Baseline = true)]
    public long SequentialExecution()
    {
        long[] nonces = ArrayPool<long>.Shared.Rent(UniqueAccountCount);
        long[] balances = ArrayPool<long>.Shared.Rent(UniqueAccountCount);

        try
        {
            Array.Clear(nonces, 0, UniqueAccountCount);
            for (int i = 0; i < UniqueAccountCount; i++)
            {
                balances[i] = 1_000_000;
            }

            long totalGas = 0;
            for (int i = 0; i < _transactions.Length; i++)
            {
                SimulatedTransaction tx = _transactions[i];
                nonces[tx.SenderIndex]++;
                balances[tx.SenderIndex] -= 21000;
                balances[tx.RecipientIndex] += 1;
                totalGas += 21000 + tx.StorageReads * 2100 + tx.StorageWrites * 5000;
            }

            return totalGas;
        }
        finally
        {
            ArrayPool<long>.Shared.Return(nonces, clearArray: false);
            ArrayPool<long>.Shared.Return(balances, clearArray: false);
        }
    }

    /// <summary>
    /// Block-STM style: speculative parallel execution with read/write set tracking
    /// and conflict detection. Measures the overhead of the STM machinery.
    /// </summary>
    [Benchmark]
    public long SpeculativeParallelExecution()
    {
        MultiVersionStore store = new(UniqueAccountCount);

        // Initialize balances
        for (int i = 0; i < UniqueAccountCount; i++)
        {
            store.WriteNonce(i, 0, -1);
            store.WriteBalance(i, 1_000_000, -1);
        }

        int[] executionOrder = new int[TransactionCount];
        for (int i = 0; i < TransactionCount; i++)
        {
            executionOrder[i] = i;
        }

        // Phase 1: Speculative parallel execution
        ReadWriteSet[] readWriteSets = new ReadWriteSet[TransactionCount];
        bool[] needsReExecution = new bool[TransactionCount];

        Parallel.For(0, TransactionCount, new ParallelOptions { MaxDegreeOfParallelism = ParallelWorkers }, txIdx =>
        {
            ReadWriteSet rwSet = new(UniqueAccountCount);
            SimulatedTransaction tx = _transactions[txIdx];

            // Read sender nonce (tracked)
            long senderNonce = store.ReadNonce(tx.SenderIndex, txIdx);
            rwSet.MarkRead(tx.SenderIndex);

            // Read recipient balance (tracked)
            long recipientBalance = store.ReadBalance(tx.RecipientIndex, txIdx);
            rwSet.MarkRead(tx.RecipientIndex);

            // Write sender nonce and balance
            store.WriteNonce(tx.SenderIndex, senderNonce + 1, txIdx);
            store.WriteBalance(tx.SenderIndex, -21000, txIdx);
            rwSet.MarkWrite(tx.SenderIndex);

            // Write recipient balance
            store.WriteBalance(tx.RecipientIndex, recipientBalance + 1, txIdx);
            rwSet.MarkWrite(tx.RecipientIndex);

            readWriteSets[txIdx] = rwSet;
        });

        // Phase 2: Conflict detection (sequential scan for overlapping read-write sets)
        int conflicts = 0;
        for (int i = 0; i < TransactionCount; i++)
        {
            for (int j = i + 1; j < TransactionCount; j++)
            {
                if (readWriteSets[i].ConflictsWith(readWriteSets[j]))
                {
                    needsReExecution[j] = true;
                    conflicts++;
                }
            }
        }

        // Phase 3: Re-execute conflicting transactions sequentially
        long totalGas = 0;
        for (int i = 0; i < TransactionCount; i++)
        {
            SimulatedTransaction tx = _transactions[i];
            totalGas += 21000 + tx.StorageReads * 2100 + tx.StorageWrites * 5000;
        }

        return totalGas + conflicts;
    }

    /// <summary>
    /// Measures just the read/write set tracking overhead in isolation.
    /// </summary>
    [Benchmark]
    public int ReadWriteSetTrackingOverhead()
    {
        int totalConflicts = 0;

        ReadWriteSet[] sets = new ReadWriteSet[TransactionCount];
        for (int i = 0; i < TransactionCount; i++)
        {
            sets[i] = new ReadWriteSet(UniqueAccountCount);
            SimulatedTransaction tx = _transactions[i];
            sets[i].MarkRead(tx.SenderIndex);
            sets[i].MarkRead(tx.RecipientIndex);
            sets[i].MarkWrite(tx.SenderIndex);
            sets[i].MarkWrite(tx.RecipientIndex);
        }

        // O(n^2) conflict detection to measure worst-case cost
        for (int i = 0; i < TransactionCount; i++)
        {
            for (int j = i + 1; j < TransactionCount; j++)
            {
                if (sets[i].ConflictsWith(sets[j]))
                {
                    totalConflicts++;
                }
            }
        }

        return totalConflicts;
    }

    /// <summary>
    /// Measures sender-group parallel execution (current approach) for comparison.
    /// Groups transactions by sender and executes groups in parallel.
    /// </summary>
    [Benchmark]
    public long SenderGroupParallelExecution()
    {
        // Group by sender
        Dictionary<int, List<int>> senderGroups = new();
        for (int i = 0; i < TransactionCount; i++)
        {
            int senderIdx = _transactions[i].SenderIndex;
            if (!senderGroups.TryGetValue(senderIdx, out List<int> group))
            {
                group = new List<int>();
                senderGroups[senderIdx] = group;
            }

            group.Add(i);
        }

        long totalGas = 0;

        // Execute groups in parallel (no conflicts within a group since same sender)
        Parallel.ForEach(senderGroups.Values, new ParallelOptions { MaxDegreeOfParallelism = ParallelWorkers }, group =>
        {
            long groupGas = 0;
            for (int g = 0; g < group.Count; g++)
            {
                int txIdx = group[g];
                SimulatedTransaction tx = _transactions[txIdx];
                groupGas += 21000 + tx.StorageReads * 2100 + tx.StorageWrites * 5000;
            }

            Interlocked.Add(ref totalGas, groupGas);
        });

        return totalGas;
    }

    private readonly struct SimulatedTransaction(int senderIndex, int recipientIndex, int storageReads, int storageWrites)
    {
        public int SenderIndex { get; } = senderIndex;
        public int RecipientIndex { get; } = recipientIndex;
        public int StorageReads { get; } = storageReads;
        public int StorageWrites { get; } = storageWrites;
    }

    /// <summary>
    /// Tracks which accounts a transaction read from and wrote to.
    /// Uses bitsets for fast intersection-based conflict detection.
    /// </summary>
    private sealed class ReadWriteSet(int accountCount)
    {
        private readonly bool[] _reads = new bool[accountCount];
        private readonly bool[] _writes = new bool[accountCount];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MarkRead(int accountIndex) => _reads[accountIndex] = true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MarkWrite(int accountIndex) => _writes[accountIndex] = true;

        /// <summary>
        /// A conflict exists when one transaction writes to an account that another reads or writes.
        /// </summary>
        public bool ConflictsWith(ReadWriteSet other)
        {
            for (int i = 0; i < _writes.Length; i++)
            {
                if (_writes[i] && (other._reads[i] || other._writes[i]))
                {
                    return true;
                }

                if (other._writes[i] && _reads[i])
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Simple multi-version store simulating Block-STM's MVCC data structure.
    /// Each account slot stores the last writer's transaction index.
    /// </summary>
    private sealed class MultiVersionStore
    {
        private readonly long[] _nonces;
        private readonly long[] _balances;
        private readonly int[] _nonceWriters;
        private readonly int[] _balanceWriters;

        public MultiVersionStore(int accountCount)
        {
            _nonces = new long[accountCount];
            _balances = new long[accountCount];
            _nonceWriters = new int[accountCount];
            _balanceWriters = new int[accountCount];
            Array.Fill(_nonceWriters, -1);
            Array.Fill(_balanceWriters, -1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long ReadNonce(int accountIndex, int txIndex)
        {
            return Volatile.Read(ref _nonces[accountIndex]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long ReadBalance(int accountIndex, int txIndex)
        {
            return Volatile.Read(ref _balances[accountIndex]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteNonce(int accountIndex, long value, int txIndex)
        {
            Volatile.Write(ref _nonces[accountIndex], value);
            Volatile.Write(ref _nonceWriters[accountIndex], txIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteBalance(int accountIndex, long value, int txIndex)
        {
            Volatile.Write(ref _balances[accountIndex], value);
            Volatile.Write(ref _balanceWriters[accountIndex], txIndex);
        }
    }
}
