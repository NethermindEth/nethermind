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
using Nethermind.Pbt;
using Nethermind.State;
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
        // Consecutive storage-zone slots (64 + s): all share one PBT storage stem (treeIndex slot>>8 == 0).
        Dense,
        // Slots spaced by 256 (64 + s*256): each lands in a distinct storage stem — maximal stem fan-out.
        Spread
    }

    private readonly CancellationTokenSource _cts = new();

    private SnapshotableMemColumnsDb<PbtColumns>? _pbtDb;
    private PbtDbManager? _pbtManager;
    private IWorldStateScopeProvider _provider = null!;

    private BlockHeader _baseHeader = null!;
    private Address[] _addresses = null!;

    [Params(Backend.Trie, Backend.Pbt)]
    public Backend StateBackend { get; set; }

    [Params(100, 500)]
    public int AccountCount { get; set; }

    // 0 isolates pure account merkelization; >0 also exercises the storage path.
    [Params(0, 20)]
    public int StorageSlotsPerAccount { get; set; }

    // Only meaningful when StorageSlotsPerAccount > 0.
    [Params(SlotLayout.Dense, SlotLayout.Spread)]
    public SlotLayout StorageLayout { get; set; }

    // Writes are flat in the layer count, so pinned to 1.
    [Params(1)]
    public int ChainDepth { get; set; }

    // Only meaningful for the Pbt backend: 1 folds the root on the calling thread, 0 takes the
    // processor count. A batch below the 128-stem threshold folds serially either way.
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

        // Build the base state as ChainDepth committed blocks, so scopes have a non-trivial base, reads
        // have data, and the layer chain under them is as deep as the parameter asks. Nothing is ever
        // finalized here, so nothing persists and the layers all stay in memory.
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
        PbtConfig config = new();
        PbtSnapshotRepository repository = new();
        PbtRocksDbPersistence persistence = new(_pbtDb);
        PbtResourcePool resourcePool = new(config);
        PbtCompactionSchedule schedule = new(new MemDb(), config, LimboLogs.Instance);
        PbtSnapshotCompactor compactor = new(resourcePool, schedule, repository, config);
        PbtPersistenceCoordinator coordinator = new(
            config, new BenchFinalizedStateProvider(), persistence, repository, compactor, schedule,
            NullStatePersistenceBarrier.Instance, LimboLogs.Instance);
        _pbtManager = new PbtDbManager(
            repository, coordinator, persistence, resourcePool, compactor, new BenchProcessExitSource(_cts), LimboLogs.Instance);
        return new PbtScopeProvider(
            new MemDb(), _pbtManager, resourcePool, PbtResourcePool.Usage.MainBlockProcessing, isReadOnly: false,
            config.InterleaveTrieNodeLevels ? PbtGroupFormat.Interleaved : PbtGroupFormat.EveryLevel, RootFoldConcurrency);
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
