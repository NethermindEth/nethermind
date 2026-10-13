// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using FlatSnapshot = Nethermind.State.Flat.Snapshot;
using static Nethermind.Benchmarks.State.FlatWorldStateBenchmarkHarness;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// State-trie hashing and commit at the end of a block, comparing a bare <see cref="FlatWorldStateScope.Commit"/>,
/// <see cref="FlatWorldStateScope.UpdateRootHash"/> followed by it, and <see cref="FlatWorldStateScope.UpdateRootHashAndCommit"/>.
/// </summary>
/// <remarks>The timed region includes the account write batch, which is the same in every arm, so read it as an A/B.</remarks>
[MemoryDiagnoser]
[NoTieredCompilation]
[MinIterationCount(40)]
public class StateRootCommitBenchmark
{
    private const int AccountCount = 100_000;
    private const ulong BaseBlockNumber = 1;

    private FlatDbConfig _config = null!;
    private ResourcePool _resourcePool = null!;
    private List<FlatSnapshot> _baseSnapshots = null!;
    private StateId _baseStateId;
    private Address[] _addresses = null!;

    private FlatWorldStateScope _scope = null!;

    [Params(16, 256, 2048)]
    public int ChangedAccounts { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        RequireTieredCompilationDisabled();

        _config = new FlatDbConfig();
        _resourcePool = new ResourcePool(_config);
        _baseSnapshots = new List<FlatSnapshot>(1);
        _addresses = new Address[AccountCount];
        for (int i = 0; i < AccountCount; i++) _addresses[i] = DeriveAddress(i + 1);

        CapturingCommitTarget commitTarget = new();
        FlatWorldStateScope scope = CreateScope(new StateId(0, Keccak.EmptyTreeHash), [], commitTarget);
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(AccountCount))
        {
            for (int i = 0; i < AccountCount; i++) batch.Set(_addresses[i], new Account(balance: (UInt256)(ulong)(i + 1)));
        }

        scope.Commit(blockNumber: BaseBlockNumber);

        FlatSnapshot snapshot = commitTarget.LastSnapshot
            ?? throw new InvalidOperationException("GlobalSetup: commit produced no snapshot");
        snapshot.TryAcquire();
        _baseSnapshots.Add(snapshot);
        _baseStateId = new StateId(BaseBlockNumber, scope.RootHash);
    }

    [IterationSetup]
    public void IterationSetup() => _scope = CreateScope(_baseStateId, _baseSnapshots, new CapturingCommitTarget());

    [IterationCleanup]
    public void IterationCleanup()
    {
        _scope?.Dispose();
        _scope = null!;
    }

    [Benchmark]
    public Hash256 Commit()
    {
        WriteChangedAccounts();
        _scope.Commit(blockNumber: BaseBlockNumber + 1);
        return _scope.RootHash;
    }

    [Benchmark(Baseline = true)]
    public Hash256 UpdateRootHashThenCommit()
    {
        WriteChangedAccounts();
        _scope.UpdateRootHash();
        _scope.Commit(blockNumber: BaseBlockNumber + 1);
        return _scope.RootHash;
    }

    [Benchmark]
    public Hash256 UpdateRootHashAndCommit()
    {
        WriteChangedAccounts();
        _scope.UpdateRootHashAndCommit(blockNumber: BaseBlockNumber + 1);
        return _scope.RootHash;
    }

    private void WriteChangedAccounts()
    {
        using IWorldStateScopeProvider.IWorldStateWriteBatch batch = _scope.StartWriteBatch(ChangedAccounts);
        // A stride spreads the changed accounts over the whole trie rather than one corner of the address list.
        int stride = AccountCount / ChangedAccounts;
        for (int i = 0; i < ChangedAccounts; i++) batch.Set(_addresses[i * stride], new Account(balance: (UInt256)(ulong)(1_000_000 + i)));
    }

    private FlatWorldStateScope CreateScope(StateId stateId, List<FlatSnapshot> baseSnapshots, CapturingCommitTarget commitTarget)
    {
        SnapshotPooledList prevSnapshots = new(baseSnapshots.Count);
        foreach (FlatSnapshot s in baseSnapshots)
        {
            s.TryAcquire();
            prevSnapshots.Add(s);
        }

        ReadOnlySnapshotBundle readOnly = new(
            prevSnapshots, new NoopPersistenceReader(), recordDetailedMetrics: false,
            PersistedSnapshotStack.Empty());
        SnapshotBundle bundle = new(readOnly, new NullTrieNodeCache(), _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        return new FlatWorldStateScope(
            currentStateId: stateId,
            snapshotBundle: bundle,
            codeDb: new NullCodeDb(),
            commitTarget: commitTarget,
            configuration: _config,
            trieCacheWarmer: new NoopTrieWarmer(),
            logManager: NullLogManager.Instance);
    }
}
