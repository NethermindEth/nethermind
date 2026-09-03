// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.Pbt;
using Nethermind.State;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Pbt;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.ScopeProvider;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Compares the EIP-8297 partitioned-binary-tree scope provider (<see cref="PbtScopeProvider"/>)
/// against the plain Merkle-Patricia trie scope provider (<see cref="TrieStoreScopeProvider"/>)
/// at the <see cref="IWorldStateScopeProvider"/> level.
/// </summary>
[MemoryDiagnoser]
[WarmupCount(3)]
[MinIterationCount(3)]
[MaxIterationCount(10)]
public class PbtScopeProviderBenchmark
{
    public enum Backend
    {
        Trie,
        Pbt
    }

    public enum SlotLayout
    {
        // Consecutive storage-zone slots share one PBT storage stem.
        Dense,
        // Slots spaced by 256 maximize PBT storage-stem fan-out.
        Spread
    }

    private readonly CancellationTokenSource _cts = new();

    private SnapshotableMemColumnsDb<PbtColumns>? _pbtDb;
    private PbtDbManager? _pbtManager;
    private PbtStoreCache? _pbtStoreCache;
    private IWorldStateScopeProvider _provider = null!;

    private BlockHeader _baseHeader = null!;
    private Address[] _addresses = null!;

    [Params(Backend.Trie, Backend.Pbt)]
    public Backend StateBackend { get; set; }

    [Params(100, 500)]
    public int AccountCount { get; set; }

    // Zero measures account merkelization only; positive values also exercise storage.
    [Params(0, 20)]
    public int StorageSlotsPerAccount { get; set; }

    [Params(SlotLayout.Dense, SlotLayout.Spread)]
    public SlotLayout StorageLayout { get; set; }

    // State writes scale linearly with layer depth, so this benchmark fixes depth at one.
    /// <summary>Which layout the PBT backend stores its nodes in; ignored by the trie backend.</summary>
    [Params(
        PbtTrieLayout.FourLevelEveryLevel,
        PbtTrieLayout.FourLevelInterleaved,
        PbtTrieLayout.FiveLevelInterleaved,
        PbtTrieLayout.SixLevelInterleaved,
        PbtTrieLayout.SixLevelEvery3Depth,
        PbtTrieLayout.EightLevelInterleaved)]
    public PbtTrieLayout Layout { get; set; }

    [Params(1)]
    public int ChainDepth { get; set; }

    // PBT only: 1 folds on the calling thread; 0 uses processor count. Batches under 1024 stems fold serially.
    [Params(1, 0)]
    public int RootFoldConcurrency { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _provider = StateBackend switch
        {
            Backend.Pbt => CreatePbtProvider(),
            Backend.Trie => new TrieStoreScopeProvider(new TestRawTrieStore(new MemDb()), new MemDb(), LimboLogs.Instance),
            _ => throw new ArgumentOutOfRangeException(nameof(StateBackend))
        };

        _addresses = new Address[AccountCount];
        for (int i = 0; i < AccountCount; i++)
        {
            _addresses[i] = DeriveAddress(i + 1);
        }

        // Commit layers without finalizing so the measured scope has an in-memory chain of the requested depth.
        Hash256 baseRoot;
        using (IWorldStateScopeProvider.IScope scope = _provider.BeginScope(null, new LocalMetrics()))
        {
            for (int block = 1; block <= ChainDepth; block++)
            {
                WriteState(scope, nonceOffset: block);
                scope.UpdateRootHash();
                scope.Commit((ulong)block);
            }

            baseRoot = scope.RootHash;
        }

        _baseHeader = Build.A.BlockHeader.WithNumber((ulong)ChainDepth).WithStateRoot(baseRoot).TestObject;
    }

    private IWorldStateScopeProvider CreatePbtProvider()
    {
        _pbtDb = new SnapshotableMemColumnsDb<PbtColumns>("pbt");
        PbtConfig config = new() { TrieNodeLayout = Layout };
        PbtSnapshotRepository repository = new();
        PbtRocksDbPersistence persistence = new(_pbtDb, config);
        PbtResourcePool resourcePool = new(config);
        _pbtStoreCache = new PbtStoreCache(config);
        PbtCompactionSchedule schedule = new(new MemDb(), config, LimboLogs.Instance);
        PbtSnapshotCompactor compactor = new(resourcePool, schedule, repository, config);
        PbtPersistenceCoordinator coordinator = new(
            config, new BenchFinalizedStateProvider(), persistence, repository, compactor, schedule,
            NullStatePersistenceBarrier.Instance, LimboLogs.Instance);
        _pbtManager = new PbtDbManager(
            repository, coordinator, persistence, resourcePool, _pbtStoreCache, compactor, new BenchProcessExitSource(_cts), new MetricsConfig(), LimboLogs.Instance);
        return new PbtScopeProvider(
            new MemDb(), _pbtManager, NullPbtChildHeaderSource.Instance, resourcePool, PbtResourcePool.Usage.MainBlockProcessing, isReadOnly: false,
            config.TrieNodeLayout, RootFoldConcurrency, new NoopTrieWarmer());
    }

    [Benchmark]
    public Hash256 WriteAndUpdateRootHash()
    {
        using IWorldStateScopeProvider.IScope scope = _provider.BeginScope(_baseHeader, new LocalMetrics());
        WriteState(scope);
        scope.UpdateRootHash();
        return scope.RootHash;
    }

    [Benchmark]
    public Account? ReadAccounts()
    {
        using IWorldStateScopeProvider.IScope scope = _provider.BeginScope(_baseHeader, new LocalMetrics());
        Account? last = null;
        for (int i = 0; i < AccountCount; i++)
        {
            last = scope.Get(_addresses[i]);
        }

        return last;
    }

    private void WriteState(IWorldStateScopeProvider.IScope scope, int nonceOffset = 0)
    {
        using IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(AccountCount);
        for (int i = 0; i < AccountCount; i++)
        {
            batch.Set(_addresses[i], new Account((ulong)(i + 1 + nonceOffset), (UInt256)(ulong)(i + 1)));

            if (StorageSlotsPerAccount > 0)
            {
                using IWorldStateScopeProvider.IStorageWriteBatch storageBatch =
                    batch.CreateStorageWriteBatch(_addresses[i], estimatedEntries: StorageSlotsPerAccount);
                for (int s = 0; s < StorageSlotsPerAccount; s++)
                {
                    storageBatch.Set(SlotKey(s), new byte[] { (byte)((s + 1) & 0xFF) });
                }
            }
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        if (_pbtManager is not null)
        {
            _cts.Cancel();
            _pbtManager.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _pbtStoreCache!.Dispose();
            _pbtDb!.Dispose();
        }

        _cts.Dispose();
    }

    private const ulong StorageZoneBase = 64;

    private UInt256 SlotKey(int s) => StorageLayout switch
    {
        SlotLayout.Dense => (UInt256)(StorageZoneBase + (ulong)s),
        SlotLayout.Spread => (UInt256)(StorageZoneBase + (ulong)s * 256),
        _ => throw new ArgumentOutOfRangeException(nameof(StorageLayout))
    };

    private static Address DeriveAddress(int index) =>
        new(Keccak.Compute(Address.FromNumber((UInt256)(ulong)index).Bytes));

    private sealed class BenchFinalizedStateProvider : IFinalizedStateProvider
    {
        public ulong FinalizedBlockNumber { get; }

        public Hash256? GetFinalizedStateRootAt(ulong blockNumber) => null;
    }

    private sealed class BenchProcessExitSource(CancellationTokenSource cts) : IProcessExitSource
    {
        public CancellationToken Token => cts.Token;

        public void Exit(int exitCode) => throw new NotSupportedException();
    }
}
