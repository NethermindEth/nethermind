// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Benchmarks.State.FlatBase;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Phase-0 go/no-go micro-benchmark for the "MDBX-like flat persistence" workstream: uniform-random
/// point reads (account 20-byte keys, storage-slot 52-byte keys; hits and guaranteed misses; 1/8/32
/// reader threads) over three backends holding byte-identical data — production-tuned RocksDB flat
/// columns, the sharded <c>SortedTable</c> arena prototype, and LMDB. See
/// <c>State/FlatBase/README.md</c> for the full-scale dataset procedure, cold-run methodology
/// (dropping the OS page cache), metrics to record, and the go/no-go thresholds.
/// </summary>
/// <remarks>
/// The dataset is built once by <see cref="FlatBaseBenchmarkDatasetBuilder"/> (scale/location via
/// <c>NETH_FLATBENCH_SCALE</c>/<c>NETH_FLATBENCH_DIR</c>) and validated byte-for-byte on every setup.
/// Each invocation performs <see cref="TotalReads"/> reads split evenly across
/// <see cref="Threads"/> workers, walking pre-generated key pools; results are per-read
/// (<c>OperationsPerInvoke</c>). Warm in-process numbers are indicative only — the decision numbers
/// come from cold runs at the <c>full</c> scale on Linux.
/// </remarks>
[MemoryDiagnoser]
[WarmupCount(2)]
[MinIterationCount(3)]
[MaxIterationCount(10)]
public class FlatBasePointReadBenchmark
{
    public enum BackendKind
    {
        RocksDb,
        SortedArena,
        Lmdb,
    }

    private const int TotalReads = 8192;
    private const int PoolSize = 1 << 15;
    private const int PoolMask = PoolSize - 1;
    private const ulong AccountPickSalt = 0xACC0_0517_BEEF_F00DUL;
    private const ulong SlotPickSalt = 0x510C_C817_1234_ABCDUL;

    [Params(BackendKind.RocksDb, BackendKind.SortedArena, BackendKind.Lmdb)]
    public BackendKind Backend;

    [Params(false, true)]
    public bool Miss;

    [Params(1, 8, 32)]
    public int Threads;

    private FlatBaseDatasetSpec _spec;
    private IFlatPointReadBackend _backend;
    private byte[] _accountKeys;
    private byte[] _slotKeys;
    private long[] _threadSums;
    private long _cursor;

    [GlobalSetup]
    public void Setup()
    {
        _spec = FlatBaseBenchmarkDatasetBuilder.EnsureBuilt();
        _backend = Backend switch
        {
            BackendKind.RocksDb => new RocksDbFlatBackend(_spec.RocksDbDir),
            BackendKind.SortedArena => SortedArenaBackend.OpenRead(_spec.ArenaDir),
            BackendKind.Lmdb => LmdbFlatBackend.OpenRead(_spec.LmdbDir, _spec.LmdbMapSize),
            _ => throw new ArgumentOutOfRangeException(nameof(Backend)),
        };

        // Correctness gate: every backend must return byte-identical values for known keys.
        FlatBaseBenchmarkDatasetBuilder.Validate(_spec, _backend, sampleCount: 1000);

        BuildKeyPools();
        _threadSums = new long[Threads];

        // A hit pool read must return data and a miss pool read must not — fail loudly, not measure garbage.
        using IFlatReadSession session = _backend.BeginSession();
        Span<byte> buffer = stackalloc byte[256];
        int length = session.GetAccount(_accountKeys.AsSpan(0, FlatBaseBenchmarkDatasetBuilder.AccountKeyLength), buffer);
        if (Miss ? length != 0 : length == 0)
            throw new InvalidOperationException($"Key pool sanity check failed: Miss={Miss}, first account read returned {length} bytes");
    }

    [GlobalCleanup]
    public void Cleanup() => _backend?.Dispose();

    /// <summary>Pre-generate uniform-random key pools. Hits pick written counters/slot indices;
    /// misses pick counters ≥ AccountCount (accounts) or slot indices ≥ SlotsPerAccount on live
    /// accounts (slots) — domains the builder never writes.</summary>
    private void BuildKeyPools()
    {
        const int accountKeyLength = FlatBaseBenchmarkDatasetBuilder.AccountKeyLength;
        const int storageKeyLength = FlatBaseBenchmarkDatasetBuilder.StorageKeyLength;
        _accountKeys = new byte[PoolSize * accountKeyLength];
        _slotKeys = new byte[PoolSize * storageKeyLength];

        long accountCount = _spec.AccountCount;
        int slotsPerAccount = _spec.SlotsPerAccount;
        bool miss = Miss;
        byte[] accountKeys = _accountKeys;
        byte[] slotKeys = _slotKeys;
        Parallel.For(0, PoolSize, i =>
        {
            ulong pick = FlatBaseBenchmarkDatasetBuilder.SplitMix64((ulong)i ^ AccountPickSalt);
            long counter = (long)(pick % (ulong)accountCount) + (miss ? accountCount : 0);
            FlatBaseBenchmarkDatasetBuilder.WriteAccountKey(counter, accountKeys.AsSpan(i * accountKeyLength, accountKeyLength));

            // Slot misses target live accounts with absent slots — the realistic miss shape.
            ulong slotPick = FlatBaseBenchmarkDatasetBuilder.SplitMix64((ulong)i ^ SlotPickSalt);
            long slotAccount = (long)(slotPick % (ulong)accountCount);
            long slotIndex = (long)(FlatBaseBenchmarkDatasetBuilder.SplitMix64(slotPick) % (ulong)slotsPerAccount)
                             + (miss ? slotsPerAccount : 0);

            Span<byte> slotAccountKey = stackalloc byte[accountKeyLength];
            Span<byte> slotHash = stackalloc byte[32];
            FlatBaseBenchmarkDatasetBuilder.WriteAccountKey(slotAccount, slotAccountKey);
            FlatBaseBenchmarkDatasetBuilder.WriteSlotHash(slotAccount, slotIndex, slotHash);
            FlatBaseBenchmarkDatasetBuilder.WriteStorageKey(
                slotAccountKey, slotHash, slotKeys.AsSpan(i * storageKeyLength, storageKeyLength));
        });
    }

    [Benchmark(OperationsPerInvoke = TotalReads)]
    public long AccountPointRead() => Run(slots: false);

    [Benchmark(OperationsPerInvoke = TotalReads)]
    public long SlotPointRead() => Run(slots: true);

    private long Run(bool slots)
    {
        long start = _cursor;
        _cursor += TotalReads;

        if (Threads == 1)
        {
            using IFlatReadSession session = _backend.BeginSession();
            return ReadRange(session, start, TotalReads, slots);
        }

        int perThread = TotalReads / Threads;
        long[] sums = _threadSums;
        Parallel.For(0, Threads, t =>
        {
            using IFlatReadSession session = _backend.BeginSession();
            sums[t] = ReadRange(session, start + t * perThread, perThread, slots);
        });

        long total = 0;
        foreach (long sum in sums)
            total += sum;
        return total;
    }

    private long ReadRange(IFlatReadSession session, long start, int count, bool slots)
    {
        const int accountKeyLength = FlatBaseBenchmarkDatasetBuilder.AccountKeyLength;
        const int storageKeyLength = FlatBaseBenchmarkDatasetBuilder.StorageKeyLength;
        Span<byte> buffer = stackalloc byte[256];
        long sum = 0;
        if (slots)
        {
            byte[] keys = _slotKeys;
            for (int i = 0; i < count; i++)
            {
                int index = (int)((start + i) & PoolMask);
                sum += session.GetSlot(keys.AsSpan(index * storageKeyLength, storageKeyLength), buffer);
            }
        }
        else
        {
            byte[] keys = _accountKeys;
            for (int i = 0; i < count; i++)
            {
                int index = (int)((start + i) & PoolMask);
                sum += session.GetAccount(keys.AsSpan(index * accountKeyLength, accountKeyLength), buffer);
            }
        }

        return sum;
    }
}
