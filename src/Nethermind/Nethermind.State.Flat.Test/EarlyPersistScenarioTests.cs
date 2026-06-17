// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Autofac;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// End-to-end scenario for early persistence with reverse diffs: blocks are committed through the real
/// scope provider, persisted at finalized compaction boundaries far closer to head than the snap-serving
/// window, and historical states below the persisted state are then read back (values and snap ranges
/// with proofs) through the reverse-diff chain.
/// </summary>
[TestFixture]
public class EarlyPersistScenarioTests
{
    private const int CompactSize = 4;
    private const int ServingWindow = 8;
    private const long HeadBlock = 20;
    private const long ExpectedPersisted = 16; // last boundary at or below finalized (head - 2)
    private const long OldestServed = 12;      // chunk boundary at or below head - ServingWindow

    private readonly Address _contract = TestItem.AddressA;

    private CancellationTokenSource _cts = null!;
    private IContainer _container = null!;
    private TestFinalizedStateProvider _finalizedStateProvider = null!;
    private TestStateRootIndex _stateRootIndex = null!;
    private FlatWorldStateManager _manager = null!;
    private IFlatDbManager _flatDbManager = null!;
    private IPersistenceManager _persistenceManager = null!;
    private readonly Dictionary<long, StateId> _stateIds = [];

    [SetUp]
    public void SetUp()
    {
        FlatDbConfig config = new()
        {
            Enabled = true,
            EarlyPersist = true,
            CompactSize = CompactSize,
            CompactionOffset = 0,
            InlineCompaction = true,
            TrieWarmerWorkerCount = 0,
        };

        _cts = new CancellationTokenSource();
        _finalizedStateProvider = new TestFinalizedStateProvider();
        _stateRootIndex = new TestStateRootIndex();
        _stateIds.Clear();

        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new FlatWorldStateModule(config))
            .AddSingleton<IFlatDbConfig>(config)
            .AddSingleton<ISyncConfig>(new SyncConfig { SnapServingMaxDepth = ServingWindow })
            .AddSingleton<IBlocksConfig>(new BlocksConfig())
            .AddSingleton<IMetricsConfig>(new MetricsConfig())
            .AddSingleton<IFinalizedStateProvider>(_finalizedStateProvider)
            .AddSingleton<IFlatStateRootIndex>(_stateRootIndex)
            .AddSingleton<IPersistence>(new RocksDbPersistence(new SnapshotableMemColumnsDb<FlatDbColumns>(), LimboLogs.Instance))
            .AddSingleton<IProcessExitSource>(new CancellationTokenSourceProcessExitSource(_cts))
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IWorldStateScopeProvider.ICodeDb>(new TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb(new TestMemDb()));
        builder.RegisterInstance<IDb>(new TestMemDb()).Keyed<IDb>(DbNames.Code);
        builder.RegisterInstance<IDb>(new TestMemDb()).Keyed<IDb>(DbNames.Metadata);
        _container = builder.Build();

        _manager = _container.Resolve<FlatWorldStateManager>();
        _flatDbManager = _container.Resolve<IFlatDbManager>();
        _persistenceManager = _container.Resolve<IPersistenceManager>();
    }

    [TearDown]
    public void TearDown()
    {
        _cts.Cancel();
        _container.Dispose();
        _cts.Dispose();
    }

    [Test]
    public void EarlyPersist_ServesHistoricalStateAndSnapRangesBelowPersistedState()
    {
        ProcessChain();

        Assert.That(
            () => _persistenceManager.GetCurrentPersistedStateId().BlockNumber,
            Is.EqualTo(ExpectedPersisted).After(10000, 50),
            "persistence should advance to the last finalized boundary, far past head - SnapServingMaxDepth");

        // Pruning runs on the persistence task after the last block's job; wait for quiescence.
        Assert.That(
            () => CanGather(_stateIds[OldestServed - 1]),
            Is.False.After(10000, 50),
            "states below the serving window should be pruned");

        using (Assert.EnterMultipleScope())
        {
            // Every state in the serving window stays readable: historical states below the persisted
            // state through the reverse-diff chain, the persisted state itself, and live states above it.
            for (long number = OldestServed; number <= HeadBlock; number++)
            {
                AssertStateAt(number);
            }

            Assert.That(CanGather(_stateIds[5]), Is.False, "deep historical state should be pruned");
        }

        AssertSnapRangesAt(OldestServed + 1);

        // FlushCache persists everything to head; in-window states must remain served via reverse diffs.
        _manager.FlushCache(CancellationToken.None);
        Assert.That(_persistenceManager.GetCurrentPersistedStateId().BlockNumber, Is.EqualTo(HeadBlock));
        AssertStateAt(HeadBlock - 2);
        AssertSnapRangesAt(HeadBlock - 2);
    }

    /// <summary>
    /// Commits blocks 0..<see cref="HeadBlock"/>: the contract account's balance and slot 1 change every
    /// block, and each block adds one fresh account and one fresh slot. Finalization trails head by 2.
    /// </summary>
    private void ProcessChain()
    {
        IWorldStateScopeProvider worldState = _manager.GlobalWorldState;

        BlockHeader? parent = null;
        for (long number = 0; number <= HeadBlock; number++)
        {
            using (IWorldStateScopeProvider.IScope scope = worldState.BeginScope(parent))
            {
                using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(2))
                {
                    writeBatch.Set(_contract, Build.An.Account.WithBalance(BalanceAt(number)).TestObject);
                    writeBatch.Set(AccountOfBlock(number), Build.An.Account.WithBalance(1).TestObject);

                    using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(_contract, 2);
                    storageBatch.Set(1, Slot1ValueAt(number));
                    storageBatch.Set(SlotOfBlock(number), [0xab]);
                }
                scope.Commit(number);
                parent = Build.A.BlockHeader.WithNumber(number).WithStateRoot(scope.RootHash).TestObject;
            }

            StateId stateId = new(number, parent.StateRoot!);
            _stateIds[number] = stateId;
            _stateRootIndex.Add(stateId);
            _finalizedStateProvider.SetStateRoot(number, parent.StateRoot!);
            _finalizedStateProvider.FinalizedBlockNumber = number - 2;
        }
    }

    private static UInt256 BalanceAt(long number) => (UInt256)(number + 1);
    private static byte[] Slot1ValueAt(long number) => [(byte)(number + 1)];
    private static UInt256 SlotOfBlock(long number) => (UInt256)(1000 + number);
    private static Address AccountOfBlock(long number) => Address.FromNumber((UInt256)(10000 + number));

    private bool CanGather(in StateId stateId)
    {
        if (_flatDbManager.TryGatherReadOnlySnapshotBundle(stateId, out ReadOnlySnapshotBundle? bundle))
        {
            bundle.Dispose();
            return true;
        }

        return false;
    }

    private static bool IsNullOrZero(byte[]? value) => value is null || value.IsZero();

    private void AssertStateAt(long number)
    {
        StateId stateId = _stateIds[number];
        Assert.That(_flatDbManager.TryGatherReadOnlySnapshotBundle(stateId, out ReadOnlySnapshotBundle? bundle), Is.True, $"state {number} should be gatherable");

        using (bundle)
        {
            int selfDestructIdx = bundle!.DetermineSelfDestructSnapshotIdx(_contract);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(bundle.GetAccount(_contract)?.Balance, Is.EqualTo(BalanceAt(number)), $"contract balance at {number}");
                Assert.That(bundle.GetAccount(AccountOfBlock(number)), Is.Not.Null, $"account created at {number}");
                Assert.That(bundle.GetAccount(AccountOfBlock(number + 1)), Is.Null, $"account created at {number + 1} must not leak into state {number}");

                Assert.That(bundle.GetSlot(_contract, 1, selfDestructIdx), Is.EqualTo(Slot1ValueAt(number)), $"slot 1 at {number}");
                Assert.That(bundle.GetSlot(_contract, SlotOfBlock(number), selfDestructIdx), Is.EqualTo(new byte[] { 0xab }), $"slot of block {number}");

                // Written one block later, so it is in persistence (which is ahead of this state); the
                // reverse diff's null marker must shadow it.
                Assert.That(IsNullOrZero(bundle.GetSlot(_contract, SlotOfBlock(number + 1), selfDestructIdx)), Is.True, $"slot of block {number + 1} must not leak into state {number}");
            }
        }
    }

    /// <summary>
    /// Serves account and storage ranges for a state through the snap server and validates the proofs
    /// against the historical root the same way a syncing client would.
    /// </summary>
    private void AssertSnapRangesAt(long number)
    {
        Hash256 root = _stateIds[number].StateRoot.ToCommitment();
        PatriciaSnapTrieFactory clientFactory = new(new NodeStorage(new MemDb()), LimboLogs.Instance);

        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) = _manager.SnapServer.GetAccountRanges(
            root, ValueKeccak.Zero, ValueKeccak.MaxValue, 1_000_000, CancellationToken.None);
        using IOwnedReadOnlyList<PathWithAccount> accountsDisposer = accounts;
        using IByteArrayList proofsDisposer = proofs;

        Assert.That(accounts.Count, Is.EqualTo(number + 2), $"contract + one account per block at {number}");

        (AddRangeResult result, _, List<PathWithAccount> storageRoots, _, _) = SnapProviderHelper.AddAccountRange(
            clientFactory, number, root, ValueKeccak.Zero, ValueKeccak.MaxValue, accounts, proofs);
        Assert.That(result, Is.EqualTo(AddRangeResult.OK), $"account range proof at {number}");

        PathWithAccount contractAccount = storageRoots.Find(a => a.Path == _contract.ToAccountPath)!;
        Assert.That(contractAccount, Is.Not.Null, "contract should be reported as having storage");

        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> slots, IByteArrayList? storageProofs) = _manager.SnapServer.GetStorageRanges(
            root, new[] { contractAccount }, ValueKeccak.Zero, ValueKeccak.MaxValue, 1_000_000, CancellationToken.None);
        using IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> slotsDisposer = slots;
        using IDisposable? storageProofsDisposer = storageProofs;

        Assert.That(slots.Count, Is.EqualTo(1));
        (AddRangeResult storageResult, _, _, _) = SnapProviderHelper.AddStorageRange(
            clientFactory, contractAccount, slots[0], ValueKeccak.Zero, ValueKeccak.MaxValue, storageProofs);
        Assert.That(storageResult, Is.EqualTo(AddRangeResult.OK), $"storage range proof at {number}");
    }

    private sealed class TestStateRootIndex : IFlatStateRootIndex
    {
        private readonly Dictionary<Hash256, StateId> _roots = [];

        public void Add(in StateId stateId) => _roots[stateId.StateRoot.ToCommitment()] = stateId;

        public bool TryGetStateId(Hash256 stateRoot, out StateId stateId) => _roots.TryGetValue(stateRoot, out stateId);

        public bool HasStateRoot(Hash256 stateRoot) => _roots.ContainsKey(stateRoot);
    }

    private sealed class TestFinalizedStateProvider : IFinalizedStateProvider
    {
        private readonly Dictionary<long, Hash256> _roots = [];

        public long FinalizedBlockNumber { get; set; }

        public void SetStateRoot(long blockNumber, Hash256 stateRoot) => _roots[blockNumber] = stateRoot;

        public Hash256? GetFinalizedStateRootAt(long blockNumber) => _roots.TryGetValue(blockNumber, out Hash256? root) ? root : null;
    }

    private sealed class CancellationTokenSourceProcessExitSource(CancellationTokenSource cancellationTokenSource) : IProcessExitSource
    {
        public CancellationToken Token => cancellationTokenSource.Token;

        public void Exit(int exitCode) => throw new NotSupportedException();
    }
}
