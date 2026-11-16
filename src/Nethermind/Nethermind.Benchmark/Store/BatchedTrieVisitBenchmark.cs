// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Testably.Abstractions;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// A full hash-scheme state visit through <see cref="BatchedTrieVisitor{TNodeContext}"/>, as trie verification runs it,
/// over a RocksDB state DB behind <see cref="FullPruningDb"/>, as in production.
/// </summary>
/// <remarks>
/// The row cache is off. A 4 MB block cache misses most node, index and filter blocks of the ~14 MB DB, while
/// 64 MB holds them all. With <see cref="DirectReads"/>
/// the reads also bypass the OS page cache. The state is built once under the temp directory and reused, so base and
/// branch runs read the same files.
/// </remarks>
[MemoryDiagnoser]
public class BatchedTrieVisitBenchmark
{
    private const int AccountCount = 25_000;
    private const int StorageAccountStep = 8;
    private const int SlotsPerStorageAccount = 16;

    private static readonly string DbPath = Path.Combine(Path.GetTempPath(), "nethermind-batched-trie-visit-bench");
    private static readonly string RootFile = Path.Combine(DbPath, "root.txt");

    private HyperClockCacheWrapper _blockCache = null!;
    private FullPruningDb _db = null!;
    private TrieStore _trieStore = null!;
    private StateReader _stateReader = null!;
    private BlockHeader _header = null!;

    [Params(1, 4)]
    public int Threads { get; set; }

    [Params(false, true)]
    public bool DirectReads { get; set; }

    [Params(4, 64)]
    public int BlockCacheMb { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        bool exists = File.Exists(RootFile);
        if (!exists && Directory.Exists(DbPath)) Directory.Delete(DbPath, true);

        Open(DirectReads && exists);
        INodeStorage nodeStorage = new NodeStorageFactory(INodeStorage.KeyScheme.Hash, LimboLogs.Instance).WrapKeyValueStore(_db);
        Hash256 root;
        if (exists)
        {
            root = new Hash256(File.ReadAllText(RootFile).Trim());
        }
        else
        {
            root = Populate(nodeStorage);
            _db.Flush(false);
            ((IDb)_db).Compact();
            File.WriteAllText(RootFile, root.ToString());
            if (DirectReads)
            {
                _db.Dispose();
                _blockCache.Dispose();
                Open(true);
                nodeStorage = new NodeStorageFactory(INodeStorage.KeyScheme.Hash, LimboLogs.Instance).WrapKeyValueStore(_db);
            }
        }

        TestFinalizedStateProvider finalizedStateProvider = new(64);
        _trieStore = new TrieStore(nodeStorage, No.Pruning, Persist.EveryBlock, finalizedStateProvider, new PruningConfig(), LimboLogs.Instance);
        finalizedStateProvider.TrieStore = _trieStore;
        _stateReader = new StateReader(_trieStore.AsReadOnly(), new MemDb(), LimboLogs.Instance);
        _header = new BlockHeader(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, UInt256.Zero, 1, 30_000_000, 0, []) { StateRoot = root };

        TrieStats stats = Visit();
        if (stats.AccountCount != AccountCount || stats.MissingNodes != 0)
            throw new InvalidOperationException($"Unexpected visit result: {stats}");
    }

    private void Open(bool directReads)
    {
        _blockCache = new HyperClockCacheWrapper((ulong)BlockCacheMb * 1_000_000);
        using IContainer configs = new ContainerBuilder().AddModule(new TestNethermindModule()).Build();
        IDbConfig dbConfig = configs.Resolve<IDbConfig>();
        dbConfig.StateDbRowCacheSize = 0;
        dbConfig.StateDbAdditionalRocksDbOptions = directReads ? "use_direct_reads=true;" : null;
        RocksDbFactory factory = new(configs.Resolve<IRocksDbConfigFactory>(), dbConfig, _blockCache, new RealFileSystem(), LimboLogs.Instance, DbPath);
        _db = new FullPruningDb(new DbSettings(DbNames.State, DbNames.State), factory);
    }

    private static Hash256 Populate(INodeStorage nodeStorage)
    {
        Random random = new(42);
        StateTree state = new(new RawScopedTrieStore(nodeStorage), LimboLogs.Instance);
        byte[] key = new byte[32];
        for (int i = 0; i < AccountCount; i++)
        {
            random.NextBytes(key);
            Hash256 addressHash = new(key);
            Account account = new((ulong)i, (UInt256)i);
            if (i % StorageAccountStep == 0)
            {
                PatriciaTree storage = new(new RawScopedTrieStore(nodeStorage, addressHash), LimboLogs.Instance);
                for (int j = 0; j < SlotsPerStorageAccount; j++)
                {
                    random.NextBytes(key);
                    storage.Set(key, [0x80 + 1, (byte)(j + 1)]);
                }

                storage.Commit();
                account = account.WithChangedStorageRoot(storage.RootHash);
            }

            state.Set(addressHash, account);
            if (i % 10_000 == 0) state.Commit();
        }

        state.Commit();
        return state.RootHash;
    }

    private TrieStats Visit()
    {
        TrieStatsCollector collector = new(new MemDb(), LimboLogs.Instance);
        _stateReader.RunTreeVisitor(collector, _header, new VisitingOptions
        {
            MaxDegreeOfParallelism = Threads,
            FullScanMemoryBudget = 4L.MiB,
        }, diagnostics: null);
        collector.Finish();
        return collector.Stats;
    }

    [Benchmark]
    public long VisitState() => Visit().NodesCount;

    [GlobalCleanup]
    public void Cleanup()
    {
        _trieStore.Dispose();
        _db.Dispose();
        _blockCache.Dispose();
    }
}
