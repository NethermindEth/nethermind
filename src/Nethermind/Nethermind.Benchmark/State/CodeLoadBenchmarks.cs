// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Benchmarks.State;

/// <summary>Measures one block's worth of code loads through the production code DB, code cache and <see cref="CodeInfo"/>.</summary>
/// <remarks>
/// <see cref="Workload.Attack"/> loads 58k distinct 64 KiB contracts per block, the shape of a code-size
/// cache-busting block (the devnet spent ~1.1-1.4 s on it), reshuffled from a 60k pool so blocks reuse contracts.
/// <see cref="Workload.FreshAttack"/> loads 58k contracts no block touched in the previous seven, from a ~30 GB pool;
/// where that exceeds the page cache, reads go to storage. <see cref="Workload.FreshSmall"/> loads 20k fresh 23-byte
/// delegation designators per block, where each read waits on latency rather than bandwidth.
/// <see cref="Workload.Hot"/> loads 20k contracts of 0.5-16.5 KiB drawn Zipf-skewed from a 50k pool, a proxy for
/// ordinary blocks where the code cache hits; <see cref="Workload.HotAfterAttack"/> first fills the code cache with
/// 58k fresh 64 KiB contracts, as an attack block would, untimed. Each load then reads the first byte of the execution
/// copy, as a CALL does. Code reads per block are printed at cleanup. Databases are built once under
/// <c>NETHERMIND_CODELOAD_DB</c> (default: the temp directory) and reused.
/// Setting <c>NETHERMIND_CODELOAD_DROP_CACHES=1</c> on Linux, as root, empties the page cache before each block.
/// Setting <c>NETHERMIND_CODELOAD_MASTER=1</c> restores the code DB's previous row cache, for an A/B of the
/// DB settings on the same build. <c>NETHERMIND_CODELOAD_LOADS</c>, <c>NETHERMIND_CODELOAD_PREFETCH</c> and
/// <c>NETHERMIND_CODELOAD_CACHE</c> narrow the parameters to comma-separated values.
/// </remarks>
[MemoryDiagnoser]
public class CodeLoadBenchmarks
{
    public enum Workload { Attack, FreshAttack, FreshSmall, Hot, HotAfterAttack }

    /// <summary>The code cache: one tier as before, or tiers by code size as <see cref="StaticCodeCache.Instance"/>.</summary>
    public enum CacheMode { Single, Tiered }

    /// <summary>How the block's code is read ahead of execution, as a block access list allows.</summary>
    public enum PrefetchMode
    {
        /// <summary>Execution reads each contract when it first needs it.</summary>
        None,
        /// <summary>
        /// Reader threads, sized like the warm-read pool, read the block's code through <see cref="CodePrefetcher"/> in
        /// access-list order, which is address order and so unrelated to the order execution needs it.
        /// </summary>
        Sync,
        /// <summary>The block's code is queued in access-list order to <see cref="CodePrefetcher"/>'s background readers.</summary>
        Queued,
    }

    private const int AttackLoads = 58_000;
    private const int SmallLoads = 20_000;
    private const int FreshBlocks = 8;
    private const int DesignatorLength = 23;

    [Params(12)]
    public int Threads { get; set; }

    [ParamsSource(nameof(Loads))]
    public Workload Load { get; set; }

    [ParamsSource(nameof(Prefetches))]
    public PrefetchMode Prefetch { get; set; }

    [ParamsSource(nameof(Caches))]
    public CacheMode Cache { get; set; }

    public static IEnumerable<Workload> Loads => Narrow<Workload>("NETHERMIND_CODELOAD_LOADS");
    public static IEnumerable<PrefetchMode> Prefetches => Narrow<PrefetchMode>("NETHERMIND_CODELOAD_PREFETCH");
    public static IEnumerable<CacheMode> Caches => Narrow<CacheMode>("NETHERMIND_CODELOAD_CACHE");

    private DbOnTheRocks _db = null!;
    private CountingCodeDb _codeDb = null!;
    private StaticCodeCache _codeCache = null!;
    private ValueHash256[] _pool = null!;
    private double[] _cumulativeWeight = null!;
    private ValueHash256[] _block = null!;
    private int[] _accessListOrder = null!;
    private Random _random = null!;
    private int _blockNumber;
    private int _readers;
    private bool _dropCaches;
    private bool _hot;
    private int _blocks;

    [GlobalSetup]
    public void Setup()
    {
        _hot = Load is Workload.Hot or Workload.HotAfterAttack;
        (int poolSize, int loadsPerBlock) = Load switch
        {
            Workload.Attack => (60_000, AttackLoads),
            Workload.FreshAttack => (FreshBlocks * AttackLoads, AttackLoads),
            Workload.FreshSmall => (FreshBlocks * SmallLoads, SmallLoads),
            _ => (50_000, 20_000),
        };
        string basePath = Path.Combine(
            Environment.GetEnvironmentVariable("NETHERMIND_CODELOAD_DB") ?? Path.Combine(Path.GetTempPath(), "nethermind-codeload-bench"),
            (_hot ? Workload.Hot : Load).ToString());
        // Keys are derived from the index, so a reused database needs no contract regeneration.
        string marker = Path.Combine(basePath, "complete-v2");
        bool build = !File.Exists(marker);
        if (build && Directory.Exists(basePath))
        {
            // Only an unfinished benchmark database is rebuilt; anything else under the path is left alone.
            if (!File.Exists(Path.Combine(basePath, DbNames.Code, "CURRENT")) && Directory.EnumerateFileSystemEntries(basePath).Any())
                throw new InvalidOperationException($"{basePath} is not a benchmark database; point NETHERMIND_CODELOAD_DB elsewhere.");
            Directory.Delete(basePath, recursive: true);
        }
        Directory.CreateDirectory(basePath);

        DbConfig dbConfig = new();
        if (Environment.GetEnvironmentVariable("NETHERMIND_CODELOAD_MASTER") == "1")
        {
            dbConfig.CodeDbRowCacheSize = 16UL * 1024 * 1024;
        }

        RocksDbConfigFactory configFactory = new(dbConfig, new PruningConfig(), new TestHardwareInfo(8L * 1024 * 1024 * 1024), LimboLogs.Instance, validateConfig: false);
        _db = new DbOnTheRocks(basePath, new DbSettings(DbNames.Code, DbNames.Code), dbConfig, configFactory, LimboLogs.Instance);
        _codeDb = new CountingCodeDb(new TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb(_db));

        _pool = new ValueHash256[poolSize];
        for (int i = 0; i < poolSize; i++)
        {
            int index = i;
            _pool[i] = ValueKeccak.Compute(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(in index)));
        }

        if (build) Fill(poolSize);

        _cumulativeWeight = new double[poolSize];
        double total = 0;
        for (int i = 0; i < poolSize; i++)
        {
            total += _hot ? 1 / Math.Pow(i + 1, 1.1) : 1;
            _cumulativeWeight[i] = total;
        }

        _codeCache = Cache == CacheMode.Tiered
            ? new StaticCodeCache(MemoryAllowance.SmallCodeCacheSize, MemoryAllowance.MediumCodeCacheSize, MemoryAllowance.LargeCodeCacheSize)
            : new StaticCodeCache(MemoryAllowance.CodeCacheSize);
        _block = new ValueHash256[loadsPerBlock];
        _accessListOrder = new int[loadsPerBlock];
        for (int i = 0; i < loadsPerBlock; i++) _accessListOrder[i] = i;
        _random = new Random(2);
        _readers = Math.Min(4 * Environment.ProcessorCount, 64);
        _dropCaches = OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("NETHERMIND_CODELOAD_DROP_CACHES") == "1";
        // Readers must not wait on thread-pool growth behind the executing threads.
        ThreadPool.GetMinThreads(out int workers, out int io);
        ThreadPool.SetMinThreads(Math.Max(workers, Threads + _readers + 8), io);

        void Fill(int count)
        {
            Random contents = new(1);
            byte[] code = new byte[65_536];
            IWriteBatch batch = _db.StartWriteBatch();
            for (int i = 0; i < count; i++)
            {
                int length = _hot ? 512 + contents.Next(16_384) : Load == Workload.FreshSmall ? DesignatorLength : code.Length;
                Span<byte> body = code.AsSpan(0, length);
                contents.NextBytes(body);
                batch.PutSpan(_pool[i].Bytes, body);
                if (i % 1_000 == 999)
                {
                    batch.Dispose();
                    batch = _db.StartWriteBatch();
                }
            }

            batch.Dispose();
            _db.Flush();
            _db.Compact();
            File.WriteAllText(marker, "");
        }
    }

    [IterationSetup]
    public void PickBlock()
    {
        switch (Load)
        {
            case Workload.Attack:
                _random.Shuffle(_pool);
                _pool.AsSpan(0, _block.Length).CopyTo(_block);
                break;
            case Workload.FreshAttack or Workload.FreshSmall:
                _pool.AsSpan(_blockNumber++ % FreshBlocks * _block.Length, _block.Length).CopyTo(_block);
                _random.Shuffle(_block);
                break;
            default:
                double total = _cumulativeWeight[^1];
                for (int i = 0; i < _block.Length; i++)
                {
                    int index = Array.BinarySearch(_cumulativeWeight, _random.NextDouble() * total);
                    _block[i] = _pool[index < 0 ? ~index : index];
                }
                break;
        }

        if (Load == Workload.HotAfterAttack) FloodCodeCache();
        _random.Shuffle(_accessListOrder);
        _blocks++;
        if (_dropCaches) File.WriteAllText("/proc/sys/vm/drop_caches", "1");
    }

    // Only the size of the code decides its tier, so the flood shares one buffer that is never executed.
    private void FloodCodeCache()
    {
        ReadOnlyMemory<byte> code = new byte[65_536];
        Span<byte> key = stackalloc byte[32];
        for (int i = 0; i < AttackLoads; i++)
        {
            _random.NextBytes(key);
            _codeCache.Set(new ValueHash256(key), new CodeInfo(code));
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        Console.WriteLine($"// CodeReadsPerBlock {Load} {Prefetch} {Cache}: {(double)_codeDb.Reads / Math.Max(_blocks, 1):F1}");
        _db.Dispose();
    }

    /// <summary>Counts reads from the store, by execution and prefetching alike.</summary>
    private sealed class CountingCodeDb(IWorldStateScopeProvider.ICodeDb codeDb) : IWorldStateScopeProvider.ICodeDb
    {
        private long _reads;

        public long Reads => Interlocked.Read(ref _reads);

        public ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash)
        {
            Interlocked.Increment(ref _reads);
            return codeDb.GetCode(in codeHash);
        }

        public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => codeDb.BeginCodeWrite();
    }

    private static IEnumerable<T> Narrow<T>(string variable) where T : struct, Enum =>
        Environment.GetEnvironmentVariable(variable) is { } names ? names.Split(',').Select(Enum.Parse<T>) : Enum.GetValues<T>();

    [Benchmark]
    public int ResolveBlock()
    {
        CodePrefetcher prefetcher = Prefetch == PrefetchMode.None ? null : new CodePrefetcher(_codeDb, _codeCache);
        Task prefetching = Prefetch switch
        {
            PrefetchMode.Sync => Task.Run(() => PrefetchBlock(prefetcher)),
            PrefetchMode.Queued => Task.Run(() => QueueBlock(prefetcher)),
            _ => Task.CompletedTask,
        };

        int loads = _block.Length;
        int chunk = (loads + Threads - 1) / Threads;
        int executed = 0;
        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, worker =>
        {
            int end = Math.Min((worker + 1) * chunk, loads);
            int local = 0;
            for (int i = worker * chunk; i < end; i++)
            {
                CodeInfo codeInfo = Resolve(in _block[i], prefetcher);
                local += codeInfo.ExecutionCodeSpan[0];
            }

            Interlocked.Add(ref executed, local);
        });

        prefetching.GetAwaiter().GetResult();
        prefetcher?.Stop();
        return executed;
    }

    private void QueueBlock(CodePrefetcher prefetcher)
    {
        foreach (int i in _accessListOrder) prefetcher.Enqueue(in _block[i]);
    }

    private void PrefetchBlock(CodePrefetcher prefetcher)
    {
        int[] order = _accessListOrder;
        int readers = _readers;
        Parallel.For(0, readers, new ParallelOptions { MaxDegreeOfParallelism = readers }, reader =>
        {
            for (int j = reader; j < order.Length; j += readers) prefetcher.Prefetch(in _block[order[j]]);
        });
    }

    private CodeInfo Resolve(in ValueHash256 codeHash, CodePrefetcher prefetcher)
    {
        CodeInfo codeInfo = _codeCache.Get(in codeHash);
        if (codeInfo is not null) return codeInfo;

        ReadOnlyMemory<byte> code = prefetcher is null ? default : prefetcher.Take(in codeHash);
        if (code.IsNull()) code = _codeDb.GetCode(in codeHash);
        // A stale database would otherwise time empty code instead of loads.
        if (code.IsNull()) throw new InvalidOperationException($"Code {codeHash} is missing; delete the benchmark database.");
        codeInfo = new CodeInfo(code);
        _codeCache.Set(in codeHash, codeInfo);
        return codeInfo;
    }
}
