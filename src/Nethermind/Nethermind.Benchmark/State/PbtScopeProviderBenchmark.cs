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

        // Build a base committed state once so scopes have a non-trivial base and reads have data.
        Hash256 baseRoot;
        using (IWorldStateScopeProvider.IScope scope = _provider.BeginScope(null, new LocalMetrics()))
        {
            WriteState(scope);
            scope.UpdateRootHash();
            scope.Commit(1);
            baseRoot = scope.RootHash;
        }

        _baseHeader = Build.A.BlockHeader.WithNumber(1).WithStateRoot(baseRoot).TestObject;
    }

    private IWorldStateScopeProvider CreatePbtProvider()
    {
        _pbtDb = new SnapshotableMemColumnsDb<PbtColumns>("pbt");
        PbtConfig config = new();
        PbtSnapshotRepository repository = new();
        PbtRocksDbPersistence persistence = new(_pbtDb);
        PbtResourcePool resourcePool = new(config);
        PbtPersistenceCoordinator coordinator = new(
            config, new BenchFinalizedStateProvider(), persistence, repository, new PbtSnapshotCompactor(resourcePool),
            NullStatePersistenceBarrier.Instance, LimboLogs.Instance);
        _pbtManager = new PbtDbManager(
            repository, coordinator, persistence, resourcePool, new BenchProcessExitSource(_cts), LimboLogs.Instance);
        return new PbtScopeProvider(new MemDb(), _pbtManager, PbtResourcePool.Usage.MainBlockProcessing, isReadOnly: false);
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

    private void WriteState(IWorldStateScopeProvider.IScope scope)
    {
        using IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(AccountCount);
        for (int i = 0; i < AccountCount; i++)
        {
            batch.Set(_addresses[i], new Account((ulong)(i + 1), (UInt256)(ulong)(i + 1)));

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
