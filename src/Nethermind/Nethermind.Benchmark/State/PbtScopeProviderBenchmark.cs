// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
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

    // How many uncommitted diff layers sit between the scope's base state and the persisted state.
    // A read walks them newest-first, so this is the axis a layer-count change shows up on; at depth
    // 1 there is nothing to walk and any such change is invisible. The trie backend has no layer
    // chain and is flat in this parameter, which is itself the comparison.
    [Params(1, 32)]
    public int ChainDepth { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _provider = StateBackend switch
        {
            Backend.Pbt => CreatePbtProvider(),
            Backend.Trie => CreateTrieProvider(),
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
            config.InterleaveTrieNodeLevels ? PbtGroupFormat.Interleaved : PbtGroupFormat.EveryLevel);
    }

    private static IWorldStateScopeProvider CreateTrieProvider() =>
        new TrieStoreScopeProvider(new TestRawTrieStore(new MemDb()), new MemDb(), LimboLogs.Instance);

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
                    storageBatch.Set((UInt256)(ulong)(s + 1), new byte[] { (byte)((s + 1) & 0xFF) });
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

    private static Address DeriveAddress(int index) =>
        new(Keccak.Compute(Address.FromNumber((UInt256)(ulong)index).Bytes));

    private sealed class BenchFinalizedStateProvider : IFinalizedStateProvider
    {
        private readonly Dictionary<ulong, Hash256> _roots = [];

        public ulong FinalizedBlockNumber { get; set; }

        public Hash256? GetFinalizedStateRootAt(ulong blockNumber) => _roots.GetValueOrDefault(blockNumber);
    }

    private sealed class BenchProcessExitSource(CancellationTokenSource cts) : IProcessExitSource
    {
        public CancellationToken Token => cts.Token;

        public void Exit(int exitCode) => throw new NotSupportedException();
    }
}
