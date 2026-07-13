// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class FlatWorldStateScopeProviderTests
{

    private class TestContext : IDisposable
    {
        private readonly ContainerBuilder _containerBuilder;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly SparseTrieWorker? _sparseTrieWorker;
        private readonly StateId _currentStateId;

        private IContainer? _container;
        private IContainer Container => _container ??= _containerBuilder.Build();

        public ResourcePool ResourcePool => field ??= Container.Resolve<ResourcePool>();
        public ITrieNodeCache TrieNodeCache => Container.Resolve<ITrieNodeCache>();
        public SnapshotPooledList ReadOnlySnapshots = new(0);
        public IPersistence.IPersistenceReader PersistenceReader => field ??= Container.Resolve<IPersistence.IPersistenceReader>();
        public Snapshot? LastCommittedSnapshot { get; set; }
        public TransientResource? LastCreatedCachedResource { get; set; }

        public TestContext(
            FlatDbConfig? config = null,
            ITrieWarmer? trieWarmer = null,
            StateId? currentStateId = null)
        {
            config ??= new FlatDbConfig();
            _currentStateId = currentStateId ?? new StateId(0, Keccak.EmptyTreeHash);
            if (config.UseSparseRootComputation)
            {
                _sparseTrieWorker = new SparseTrieWorker(
                    LimboLogs.Instance.GetClassLogger<SparseTrieWorker>(),
                    _cancellationTokenSource.Token);
            }

            _containerBuilder = new ContainerBuilder()
                    .AddModule(new FlatWorldStateModule(config))
                    .AddSingleton<IPersistence.IPersistenceReader>(_ => Substitute.For<IPersistence.IPersistenceReader>())
                    .AddSingleton<IFlatDbManager>((ctx) =>
                    {
                        ResourcePool resourcePool = ctx.Resolve<ResourcePool>();
                        IFlatDbManager flatDiff = Substitute.For<IFlatDbManager>();
                        flatDiff.When(it => it.AddSnapshot(Arg.Any<Snapshot>(), Arg.Any<TransientResource>()))
                            .Do(c =>
                            {
                                Snapshot snapshot = (Snapshot)c[0];
                                TransientResource transientResource = (TransientResource)c[1];

                                if (LastCommittedSnapshot is not null)
                                {
                                    LastCommittedSnapshot.Dispose();
                                }
                                LastCommittedSnapshot = snapshot;

                                if (LastCreatedCachedResource is not null)
                                {
                                    resourcePool.ReturnCachedResource(ResourcePool.Usage.MainBlockProcessing, transientResource);
                                }
                                LastCreatedCachedResource = transientResource;
                            });

                        return flatDiff;
                    })
                    .Bind<IFlatCommitTarget, IFlatDbManager>()
                    .AddSingleton<IProcessExitSource>(_ => new CancellationTokenSourceProcessExitSource(_cancellationTokenSource))
                    .AddSingleton<ILogManager>(LimboLogs.Instance)
                    .AddSingleton<IFlatDbConfig>(config)
                    .AddSingleton<IWorldStateScopeProvider.ICodeDb>(_ => new TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb(new TestMemDb()))
                    .AddSingleton<IInitConfig>(_ => Substitute.For<IInitConfig>())
                ;

            if (trieWarmer is not null)
            {
                _containerBuilder.AddSingleton(trieWarmer);
            }

            // Externally owned because snapshot bundle take ownership
            _containerBuilder.RegisterType<ReadOnlySnapshotBundle>()
                .WithParameter(TypedParameter.From(false)) // recordDetailedMetrics
                .WithParameter(TypedParameter.From(ReadOnlySnapshots))
                .WithParameter(TypedParameter.From(PersistedSnapshotStack.Empty()))
                .ExternallyOwned();

            ConfigureSnapshotBundle();
            ConfigureFlatWorldStateScope();
        }

        private void ConfigureSnapshotBundle() =>
            _containerBuilder.RegisterType<SnapshotBundle>()
                .SingleInstance()
                .WithParameter(TypedParameter.From(ResourcePool.Usage.MainBlockProcessing))
                .ExternallyOwned();

        private void ConfigureFlatWorldStateScope() => _containerBuilder.RegisterType<FlatWorldStateScope>()
                .SingleInstance()
                .WithParameter(TypedParameter.From(_currentStateId))
                .WithParameter(TypedParameter.From(_sparseTrieWorker))
                ;

        public FlatWorldStateScope Scope => Container.Resolve<FlatWorldStateScope>();

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();

            LastCommittedSnapshot?.Dispose();
            if (LastCreatedCachedResource is not null) ResourcePool.ReturnCachedResource(ResourcePool.Usage.MainBlockProcessing, LastCreatedCachedResource);

            _container?.Dispose();
            _sparseTrieWorker?.Dispose();
            _cancellationTokenSource.Dispose();
        }

        public class CancellationTokenSourceProcessExitSource(CancellationTokenSource cancellationTokenSource) : IProcessExitSource
        {
            public CancellationToken Token => cancellationTokenSource.Token;

            public void Exit(int exitCode) => throw new NotImplementedException();
        }

        public void AddSnapshot(Action<SnapshotContent> populator)
        {
            SnapshotContent snapshotContent = ResourcePool.GetSnapshotContent(ResourcePool.Usage.MainBlockProcessing);
            populator(snapshotContent);

            ReadOnlySnapshots.Add(new Snapshot(
                StateId.PreGenesis,
                StateId.PreGenesis,
                snapshotContent,
                ResourcePool,
                ResourcePool.Usage.MainBlockProcessing));
        }
    }


    #region Account and Slot Layering Tests

    [Test]
    public void TestAccountAndSlotShadowingInSnapshots()
    {
        using TestContext ctx = new();

        Address testAddress = TestItem.AddressA;
        UInt256 slotIndex = 1;

        Account olderAccount = TestItem.GenerateRandomAccount();
        byte[] olderSlotValue = { 0x01, 0x02 };

        Account newerAccount = TestItem.GenerateRandomAccount();
        byte[] newerSlotValue = { 0x03, 0x04, 0x05 };

        // Layer 1: Older snapshot
        ctx.AddSnapshot(content =>
        {
            content.Accounts[testAddress] = olderAccount;
            content.Storages[(testAddress, slotIndex)] = SlotValue.FromSpanWithoutLeadingZero(olderSlotValue);
        });

        // Layer 2: Newer snapshot (shadowing Layer 1)
        ctx.AddSnapshot(content =>
        {
            content.Accounts[testAddress] = newerAccount;
            content.Storages[(testAddress, slotIndex)] = SlotValue.FromSpanWithoutLeadingZero(newerSlotValue);
        });

        // Layer 3: Another newer snapshot, but only for account
        Account newestAccount = TestItem.GenerateRandomAccount();
        ctx.AddSnapshot(content => content.Accounts[testAddress] = newestAccount);

        // Verify account shadowed by newest snapshot (newestAccount)
        Assert.That(ctx.Scope.Get(testAddress), Is.EqualTo(newestAccount));

        // Verify slot shadowed by Layer 2 snapshot (newerSlotValue)
        IWorldStateScopeProvider.IStorageTree storageTree = ctx.Scope.CreateStorageTree(testAddress);
        Assert.That(storageTree.Get(slotIndex), Is.EqualTo(newerSlotValue));
    }

    [Test]
    public void TestAccountAndSlotFromPersistence()
    {
        using TestContext ctx = new();

        Address testAddress = TestItem.AddressA;
        UInt256 slotIndex = 1;
        Account persistedAccount = TestItem.GenerateRandomAccount();
        byte[] persistedSlotValue = { 0xDE, 0xAD, 0xBE, 0xEF };

        // Setup Persistence Reader
        ctx.PersistenceReader.GetAccount(testAddress).Returns(persistedAccount);
        SlotValue outValue = SlotValue.FromSpanWithoutLeadingZero(persistedSlotValue);
        ctx.PersistenceReader.TryGetSlot(testAddress, slotIndex, ref Arg.Any<SlotValue>())
            .Returns(x =>
            {
                x[2] = outValue;
                return true;
            });

        // Verify both are retrieved from persistence
        Assert.That(ctx.Scope.Get(testAddress), Is.EqualTo(persistedAccount));

        IWorldStateScopeProvider.IStorageTree storageTree = ctx.Scope.CreateStorageTree(testAddress);
        Assert.That(storageTree.Get(slotIndex), Is.EqualTo(persistedSlotValue));
    }

    [Test]
    public void TestAccountAndSlotFromWrittenBatch()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        UInt256 slotIndex = 1;
        Account testAccount = TestItem.GenerateRandomAccount();
        byte[] writtenSlotValue = { 0xFF, 0xFF };

        Account persistenceAccount = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(testAddress).Returns(persistenceAccount);

        // Add dummy snapshot
        ctx.AddSnapshot(content => { });

        // Write directly to write batch
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            writeBatch.Set(testAddress, testAccount);
            IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(testAddress, 1);
            storageBatch.Set(slotIndex, writtenSlotValue);
            storageBatch.Dispose();
        }

        // Verify written items shadow everything else
        Account? resultAccount = scope.Get(testAddress);
        Assert.That(resultAccount!.Balance, Is.EqualTo(testAccount.Balance));
        Assert.That(resultAccount!.Nonce, Is.EqualTo(testAccount.Nonce));

        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(testAddress);
        Assert.That(storageTree.Get(slotIndex), Is.EqualTo(writtenSlotValue));
    }

    [Test]
    public void TestAccountAndSlotAfterCommit()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        UInt256 slotIndex = 1;
        Account testAccount = TestItem.GenerateRandomAccount();
        byte[] slotValue = { 0xCA, 0xFE };

        // Write both
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            writeBatch.Set(testAddress, testAccount);
            IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(testAddress, 1);
            storageBatch.Set(slotIndex, slotValue);
            storageBatch.Dispose();
        }

        // Commit both
        scope.Commit(1);

        // Verify in snapshot
        Assert.That(ctx.LastCommittedSnapshot, Is.Not.Null);
        ctx.LastCommittedSnapshot!.TryGetAccount(testAddress, out Account? committedAccount);
        Assert.That(committedAccount!.Balance, Is.EqualTo(testAccount.Balance));
        Assert.That(committedAccount!.Nonce, Is.EqualTo(testAccount.Nonce));

        ctx.LastCommittedSnapshot!.TryGetStorage((testAddress, slotIndex), out SlotValue? committedSlot);
        Assert.That(committedSlot!.Value.ToEvmBytes(), Is.EqualTo(slotValue));
    }

    #endregion

    #region Selfdestruct Interaction Tests

    [Test]
    public void TestSelfDestructBlocksEarlierAccountAndSlot()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        UInt256 slotIndex = 1;
        Account oldAccount = TestItem.GenerateRandomAccount();
        byte[] oldSlotValue = { 0x01, 0x02, 0x03 };

        // Layer 1: Account and Slot data
        ctx.AddSnapshot(content =>
        {
            content.Accounts[testAddress] = oldAccount;
            content.Storages[(testAddress, slotIndex)] = SlotValue.FromSpanWithoutLeadingZero(oldSlotValue);
        });

        // Layer 2: SELFDESTRUCT
        // isNewAccount = false means there was storage to clear
        ctx.AddSnapshot(content => content.SelfDestructedStorageAddresses[testAddress] = false);

        // Layer 3: Empty snapshot after selfdestruct
        ctx.AddSnapshot(content => { });

        // Slot should be blocked by selfdestruct
        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(testAddress);
        Assert.That(storageTree.Get(slotIndex), Is.EqualTo(StorageTree.ZeroBytes));
    }

    [Test]
    public void TestSelfDestructIdxIsPassedCorrectly()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        UInt256 slot1 = 1;
        UInt256 slot2 = 2;
        byte[] slot1BeforeValue = { 0x01 };
        byte[] slot2AfterValue = { 0x02 };

        // Snapshot 0: slot1 exists
        ctx.AddSnapshot(content => content.Storages[(testAddress, slot1)] = SlotValue.FromSpanWithoutLeadingZero(slot1BeforeValue));

        // Snapshot 1: selfdestruct happens at this index
        ctx.AddSnapshot(content => content.SelfDestructedStorageAddresses[testAddress] = false);

        // Snapshot 2: slot2 is set after selfdestruct
        ctx.AddSnapshot(content => content.Storages[(testAddress, slot2)] = SlotValue.FromSpanWithoutLeadingZero(slot2AfterValue));

        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(testAddress);

        // slot1 should return zero (blocked by selfdestruct)
        Assert.That(storageTree.Get(slot1), Is.EqualTo(StorageTree.ZeroBytes));

        // slot2 should return the value (written after selfdestruct)
        Assert.That(storageTree.Get(slot2), Is.EqualTo(slot2AfterValue));
    }

    #endregion

    #region Storage Root Tests

    [Test]
    public void TestStorageRootAfterSingleSlotSet()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        UInt256 slotIndex = 1;
        byte[] slotValue = { 0xAB, 0xCD };

        Account initialAccount = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(testAddress).Returns(initialAccount);

        // Set a single slot
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(testAddress, 1);
            storageBatch.Set(slotIndex, slotValue);
            storageBatch.Dispose();
        }

        // Commit to update storage root
        scope.Commit(1);

        // Compute expected storage root using standalone StorageTree
        TestMemDb testDb = new();
        RawScopedTrieStore trieStore = new(testDb);
        StorageTree expectedTree = new(trieStore, LimboLogs.Instance);
        expectedTree.Set(slotIndex, slotValue);
        expectedTree.UpdateRootHash();
        Hash256 expectedRoot = expectedTree.RootHash;

        // Verify actual storage root matches expected
        Account? resultAccount = scope.Get(testAddress);
        Assert.That(resultAccount, Is.Not.Null);
        Assert.That(resultAccount!.StorageRoot, Is.EqualTo(expectedRoot));
    }

    [Test]
    public void TestStorageRootAfterMultipleSlotsSingleCommit()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        UInt256 slot1 = 1;
        UInt256 slot2 = 2;
        UInt256 slot3 = 100;
        byte[] value1 = { 0x01, 0x02 };
        byte[] value2 = { 0xAA, 0xBB, 0xCC };
        byte[] value3 = { 0xFF };

        Account initialAccount = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(testAddress).Returns(initialAccount);

        // Set multiple slots in single commit
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(testAddress, 3);
            storageBatch.Set(slot1, value1);
            storageBatch.Set(slot2, value2);
            storageBatch.Set(slot3, value3);
            storageBatch.Dispose();
        }

        scope.Commit(1);

        // Compute expected storage root
        TestMemDb testDb = new();
        RawScopedTrieStore trieStore = new(testDb);
        StorageTree expectedTree = new(trieStore, LimboLogs.Instance);
        expectedTree.Set(slot1, value1);
        expectedTree.Set(slot2, value2);
        expectedTree.Set(slot3, value3);
        expectedTree.UpdateRootHash();
        Hash256 expectedRoot = expectedTree.RootHash;

        // Verify
        Account? resultAccount = scope.Get(testAddress);
        Assert.That(resultAccount!.StorageRoot, Is.EqualTo(expectedRoot));
    }

    [Test]
    public void TestStorageRootAfterMultipleCommits()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        UInt256 slot1 = 1;
        UInt256 slot2 = 2;
        byte[] value1 = { 0x11 };
        byte[] value2 = { 0x22 };

        Account initialAccount = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(testAddress).Returns(initialAccount);

        // First commit - set slot1
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(testAddress, 1);
            storageBatch.Set(slot1, value1);
            storageBatch.Dispose();
        }
        scope.Commit(1);

        // Second commit - set slot2
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(testAddress, 1);
            storageBatch.Set(slot2, value2);
            storageBatch.Dispose();
        }
        scope.Commit(2);

        // Compute expected storage root with both slots
        TestMemDb testDb = new();
        RawScopedTrieStore trieStore = new(testDb);
        StorageTree expectedTree = new(trieStore, LimboLogs.Instance);
        expectedTree.Set(slot1, value1);
        expectedTree.Set(slot2, value2);
        expectedTree.UpdateRootHash();
        Hash256 expectedRoot = expectedTree.RootHash;

        // Verify
        Account? resultAccount = scope.Get(testAddress);
        Assert.That(resultAccount!.StorageRoot, Is.EqualTo(expectedRoot));
    }

    [Test]
    public void TestStorageRootAfterSelfDestructAndNewSlots()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        UInt256 slot1 = 1;
        UInt256 slot2 = 2;
        byte[] value1 = { 0xAA };
        byte[] value2 = { 0xBB };

        Account initialAccount = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(testAddress).Returns(initialAccount);

        // Set initial slot
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(testAddress, 1);
            storageBatch.Set(slot1, value1);
            storageBatch.Dispose();
        }
        scope.Commit(1);

        // SelfDestruct - should clear storage
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(testAddress, 0);
            storageBatch.Clear();
            storageBatch.Dispose();
        }
        scope.Commit(2);

        // Set new slot after selfdestruct
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(testAddress, 1);
            storageBatch.Set(slot2, value2);
            storageBatch.Dispose();
        }
        scope.Commit(3);

        // Expected: only slot2 should exist (storage was cleared)
        TestMemDb testDb = new();
        RawScopedTrieStore trieStore = new(testDb);
        StorageTree expectedTree = new(trieStore, LimboLogs.Instance);
        expectedTree.Set(slot2, value2);
        expectedTree.UpdateRootHash();
        Hash256 expectedRoot = expectedTree.RootHash;

        // Verify
        Account? resultAccount = scope.Get(testAddress);
        Assert.That(resultAccount!.StorageRoot, Is.EqualTo(expectedRoot));
    }

    [Test]
    public void TestEmptyStorageRootWhenNoSlots()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;

        Account initialAccount = new(0, 0);
        ctx.PersistenceReader.GetAccount(testAddress).Returns(initialAccount);

        // Don't set any slots, just get the account
        Account? resultAccount = scope.Get(testAddress);

        // Verify storage root is EmptyTreeHash
        Assert.That(resultAccount, Is.Not.Null);
        Assert.That(resultAccount!.StorageRoot, Is.EqualTo(Keccak.EmptyTreeHash));
    }

    #endregion

    #region Account Snapshot Commit Tests

    [Test]
    public void TestMultipleAccountsAndSlotsCommittedInSnapshot()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        Address addr1 = TestItem.AddressA;
        Address addr2 = TestItem.AddressB;
        Account acc1 = new(100, 1000);
        Account acc2 = new(200, 2000);
        UInt256 slot1 = 1;
        byte[] val1 = { 0x01 };

        // Set multiple items
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(2))
        {
            writeBatch.Set(addr1, acc1);
            writeBatch.Set(addr2, acc2);
            IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(addr1, 1);
            storageBatch.Set(slot1, val1);
            storageBatch.Dispose();
        }

        scope.Commit(1);

        // Verify all committed to snapshot
        Assert.That(ctx.LastCommittedSnapshot, Is.Not.Null);
        ctx.LastCommittedSnapshot!.TryGetAccount(addr1, out Account? committedAcc1);
        Assert.That(committedAcc1!.Balance, Is.EqualTo(acc1.Balance));

        ctx.LastCommittedSnapshot!.TryGetAccount(addr2, out Account? committedAcc2);
        Assert.That(committedAcc2!.Balance, Is.EqualTo(acc2.Balance));

        ctx.LastCommittedSnapshot!.TryGetStorage((addr1, slot1), out SlotValue? committedSlot);
        Assert.That(committedSlot!.Value.ToEvmBytes(), Is.EqualTo(val1));
    }

    [Test]
    public void TestMultipleCommitsAccumulateData()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        Address addr1 = TestItem.AddressA;
        Address addr2 = TestItem.AddressB;
        Account acc1 = new(100, 1000);
        Account acc2 = new(200, 2000);

        // Commit 1
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            writeBatch.Set(addr1, acc1);
        }
        scope.Commit(1);

        // Commit 2
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            writeBatch.Set(addr2, acc2);
        }
        scope.Commit(2);

        // Verify scope Sees both
        Assert.That(scope.Get(addr1), Is.EqualTo(acc1));
        Assert.That(scope.Get(addr2), Is.EqualTo(acc2));
    }

    #endregion

    #region Comprehensive Selfdestruct Blocking Tests

    [Test]
    public void TestSelfDestructBlocksPersistenceAndAllSnapshotLayers()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        Address addr = TestItem.AddressA;
        UInt256 slot = 1;
        byte[] persistedVal = { 0xDE, 0xAD };
        byte[] snapshotVal = { 0x01, 0x02 };

        // Persistence setup
        ctx.PersistenceReader.GetAccount(addr).Returns(TestItem.GenerateRandomAccount());
        SlotValue outVal = SlotValue.FromSpanWithoutLeadingZero(persistedVal);
        ctx.PersistenceReader.TryGetSlot(addr, slot, ref Arg.Any<SlotValue>())
            .Returns(x => { x[2] = outVal; return true; });

        // Snapshot Setup
        ctx.AddSnapshot(content => content.Storages[(addr, slot)] = SlotValue.FromSpanWithoutLeadingZero(snapshotVal));
        ctx.AddSnapshot(content => content.SelfDestructedStorageAddresses[addr] = true);
        ctx.AddSnapshot(content => { });

        // Verify both are blocked
        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(addr);
        Assert.That(storageTree.Get(slot), Is.EqualTo(StorageTree.ZeroBytes));
    }

    [Test]
    public void TestStorageNodeLookupWithoutSelfDestructFallsThroughToReadOnlyBundle()
    {
        // This test verifies the fix for the bug where storage node lookup would exit early
        // when selfDestructStateIdx == -1 (no self-destruct) and local _snapshots exist but
        // don't contain the storage node. Before the fix, the condition `i >= currentBundleSelfDestructIdx`
        // was always true when selfDestructStateIdx == -1, causing early exit.

        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        Address addr1 = TestItem.AddressA;
        Address addr2 = TestItem.AddressB;
        Hash256 addr1Hash = Keccak.Compute(addr1.Bytes);
        UInt256 slot1 = 1;
        byte[] value1 = { 0x01 };

        Account acc1 = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(addr1).Returns(acc1);

        // Add storage slot AND trie node for addr1 to ReadOnlySnapshots
        ctx.AddSnapshot(content =>
        {
            content.Storages[(addr1, slot1)] = SlotValue.FromSpanWithoutLeadingZero(value1);

            // Also add a storage trie node for addr1 at root path
            TrieNode storageNode = new(NodeType.Leaf, Keccak.Zero);
            content.StorageNodes[(addr1Hash, TreePath.Empty)] = storageNode;
        });

        // Create local commits for addr2 (NOT addr1) - this creates local _snapshots
        Account acc2 = TestItem.GenerateRandomAccount();
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            writeBatch.Set(addr2, acc2);
        }
        scope.Commit(1);

        // Now lookup storage for addr1 - should fall through local _snapshots to ReadOnlySnapshots
        // Before the fix: would fail because DoTryFindStorageNodeExternal exited early
        // After the fix: properly falls through and finds storage in ReadOnlySnapshots
        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(addr1);
        Assert.That(storageTree.Get(slot1), Is.EqualTo(value1));
    }

    [Test]
    public void TestSelfDestructInLocalSnapshotsStopsAtExpectedSnapshot()
    {
        // This test verifies that when self-destruct is in local _snapshots (SnapshotBundle),
        // the storage lookup correctly:
        // 1. Finds storage added AFTER self-destruct (in newer snapshots)
        // 2. Finds storage added AT the same commit as self-destruct
        // 3. Returns null for storage that existed BEFORE self-destruct (blocked by self-destruct)

        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        Address addr = TestItem.AddressA;
        UInt256 slotBefore = 1;
        UInt256 slotAtSelfDestruct = 2;
        UInt256 slotAfter = 3;
        byte[] valueBefore = { 0x01 };
        byte[] valueAtSelfDestruct = { 0x02 };
        byte[] valueAfter = { 0x03 };

        Account acc = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(addr).Returns(acc);

        // Commit 1: Set slot BEFORE self-destruct
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(addr, 1);
            storageBatch.Set(slotBefore, valueBefore);
            storageBatch.Dispose();
        }
        scope.Commit(1);

        // Commit 2: Self-destruct AND set new slot in same commit
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(addr, 1);
            storageBatch.Clear();
            storageBatch.Set(slotAtSelfDestruct, valueAtSelfDestruct);
            storageBatch.Dispose();
        }
        scope.Commit(2);

        // Commit 3: Set slot AFTER self-destruct
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(addr, 1);
            storageBatch.Set(slotAfter, valueAfter);
            storageBatch.Dispose();
        }
        scope.Commit(3);

        // Verify storage behavior:
        // - slotBefore should be blocked by self-destruct (return zero)
        // - slotAtSelfDestruct should be found (set in same commit as self-destruct)
        // - slotAfter should be found (added after self-destruct)
        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(addr);
        Assert.That(storageTree.Get(slotBefore), Is.EqualTo(StorageTree.ZeroBytes), "Slot before self-destruct should be zero");
        Assert.That(storageTree.Get(slotAtSelfDestruct), Is.EqualTo(valueAtSelfDestruct), "Slot at self-destruct should be found");
        Assert.That(storageTree.Get(slotAfter), Is.EqualTo(valueAfter), "Slot after self-destruct should be found");
    }

    [Test]
    public void TestSelfDestructInReadOnlySnapshotDoesNotBlockNewerLocalSnapshots()
    {
        // When DetermineSelfDestructSnapshotIdx finds the self-destruct in ReadOnlySnapshotBundle,
        // selfDestructStateIdx is in [0, readOnlySnapshotCount-1]. In GetSlot,
        // currentBundleSelfDestructIdx becomes negative, which previously caused the entire
        // _snapshots loop to be skipped, making storage written after self-destruct invisible.

        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        Address addr = TestItem.AddressA;
        UInt256 slotBefore = 1;
        UInt256 slotAfter1 = 2;
        UInt256 slotAfter2 = 3;
        byte[] valueBeforeSelfDestruct = { 0x01 };
        byte[] valueAfter1 = { 0x02 };
        byte[] valueAfter2 = { 0x03 };

        Account acc = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(addr).Returns(acc);

        // Read-only snapshot 0: slot exists before self-destruct
        ctx.AddSnapshot(content =>
            content.Storages[(addr, slotBefore)] = SlotValue.FromSpanWithoutLeadingZero(valueBeforeSelfDestruct));

        // Read-only snapshot 1: self-destruct marker (in ReadOnlySnapshotBundle)
        ctx.AddSnapshot(content => content.SelfDestructedStorageAddresses[addr] = false);

        // Local commit 1: write storage after self-destruct
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(addr, 1);
            storageBatch.Set(slotAfter1, valueAfter1);
            storageBatch.Dispose();
        }
        scope.Commit(1);

        // Local commit 2: write another storage slot after self-destruct
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(addr, 1);
            storageBatch.Set(slotAfter2, valueAfter2);
            storageBatch.Dispose();
        }
        scope.Commit(2);

        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(addr);

        // Slots written after self-destruct in local snapshots should be visible
        Assert.That(storageTree.Get(slotAfter1), Is.EqualTo(valueAfter1), "Slot in local snapshot after read-only self-destruct should be visible");
        Assert.That(storageTree.Get(slotAfter2), Is.EqualTo(valueAfter2), "Slot in local snapshot after read-only self-destruct should be visible");

        // Slot from before self-destruct (in read-only snapshot) should be blocked
        Assert.That(storageTree.Get(slotBefore), Is.EqualTo(StorageTree.ZeroBytes), "Slot before self-destruct should be zero");
    }

    #endregion

    [Test]
    public async Task Dispose_WaitsForOutstandingWarmups_BeforeDisposingBundle()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        // Simulate an in-flight warmup job by manually incrementing the counter.
        scope.IncrementOutstandingWarmups();

        // Use the test hook to know precisely when Dispose has entered the wait loop.
        ManualResetEventSlim waitEntered = new(false);
        scope.OnWaitingForWarmups = () => waitEntered.Set();

        bool disposeCompleted = false;
        Task disposeTask = Task.Run(() =>
        {
            scope.Dispose();
            disposeCompleted = true;
        });

        Assert.That(waitEntered.Wait(5000), Is.True, "Dispose should enter the wait loop");
        Assert.That(disposeCompleted, Is.False, "Dispose should still be blocking");

        // Simulate the warmup completing — Dispose should now unblock.
        scope.DecrementOutstandingWarmups();

        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(disposeCompleted, Is.True, "Dispose should complete after the outstanding warmup finishes");
    }

    [Test]
    public async Task Dispose_CompletesImmediately_WhenNoOutstandingWarmups()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        Task disposeTask = Task.Run(() => scope.Dispose());

        // Should complete well within 5 seconds when nothing is in flight.
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(disposeTask.IsCompletedSuccessfully, Is.True);
    }

    [Test]
    public async Task Dispose_GivesUpWaiting_ReaderOutlivesInFlightWarmup()
    {
        BlockingPersistenceReader reader = new();
        ReadOnlySnapshotBundle readOnlyBundle = new(new SnapshotPooledList(0), reader, recordDetailedMetrics: false, PersistedSnapshotStack.Empty());
        FlatDbConfig config = new();
        ResourcePool resourcePool = new(config);
        SnapshotBundle bundle = new(readOnlyBundle, Substitute.For<ITrieNodeCache>(), resourcePool, ResourcePool.Usage.MainBlockProcessing);
        await using TrieWarmer warmer = new(LimboLogs.Instance, config);

        FlatWorldStateScope scope = new(
            currentStateId: new StateId(0, TestItem.KeccakA),
            snapshotBundle: bundle,
            codeDb: new TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb(new TestMemDb()),
            commitTarget: Substitute.For<IFlatCommitTarget>(),
            configuration: config,
            trieCacheWarmer: warmer,
            sparseTrieWorker: null,
            logManager: LimboLogs.Instance);

        // Queues a state-trie warmup job whose traversal blocks inside the persistence reader,
        // simulating the slow cold read that is in flight when a restart-replay scope is disposed.
        scope.HintGet(TestItem.AddressA, null);
        Assert.That(reader.ReadEntered.Wait(30_000), Is.True, "Warmup job should reach the persistence reader");

        Task disposeTask = Task.Run(() => scope.Dispose());
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(reader.DisposedDuringActiveRead, Is.False, "Reader must not be disposed while a read is in flight");
        Assert.That(reader.IsDisposed, Is.False, "In-flight warmup lease should keep the reader alive past scope dispose");

        reader.ResumeReads.Set();

        Assert.That(() => reader.IsDisposed, Is.True.After(5000, 50), "Reader should be disposed once the warmup job completes");
    }

    [TestCase(true, false, TestName = "StorageHintSet_SlotRingAccepts_DoesNotFallBack")]
    [TestCase(false, true, TestName = "StorageHintSet_SlotRingFull_FallsBackToMpmcBuffer")]
    [TestCase(false, false, TestName = "StorageHintSet_BothBuffersFull_DropsHint")]
    public void StorageHintSet_FallsBackToMpmcBufferWhenSlotRingIsFull(bool slotRingAccepts, bool mpmcAccepts)
    {
        RecordingTrieWarmer warmer = new(slotRingAccepts, mpmcAccepts);
        using TestContext ctx = new(trieWarmer: warmer);
        FlatWorldStateScope scope = ctx.Scope;
        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(TestItem.AddressA);

        storageTree.HintSet((UInt256)1, [1]);

        Assert.That(warmer.SlotJobPushes, Is.EqualTo(1));
        Assert.That(warmer.MpmcSlotJobPushes, Is.EqualTo(slotRingAccepts ? 0 : 1));

        // The dedupe bloom is already marked, so a repeated hint for the same slot must not push again.
        storageTree.HintSet((UInt256)1, [1]);
        Assert.That(warmer.SlotJobPushes, Is.EqualTo(1));
        Assert.That(warmer.MpmcSlotJobPushes, Is.EqualTo(slotRingAccepts ? 0 : 1));

        // An accepted push must have incremented the outstanding-warmup counter (and a dropped one must not):
        // after balancing accepted pushes, Dispose should not enter the wait loop.
        bool enteredWaitLoop = false;
        scope.OnWaitingForWarmups = () => enteredWaitLoop = true;
        if (slotRingAccepts || mpmcAccepts) scope.DecrementOutstandingWarmups();
        scope.Dispose();
        Assert.That(enteredWaitLoop, Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SparseWriteBatch_MatchesPatriciaBeforeAndAfterFallback(bool sparseStorage)
    {
        using TestContext ctx = new(
            new FlatDbConfig
            {
                UseSparseRootComputation = true,
                UseSparseStorageRootComputation = sparseStorage,
            },
            new NoopTrieWarmer());
        FlatWorldStateScope scope = ctx.Scope;
        IWorldStateScopeProvider.ISparseDeltaSink sink = scope;

        Address address = TestItem.AddressA;
        UInt256 slot = 1;
        byte[] value = [0x12, 0x34];
        Account account = new(1, (UInt256)1_000);

        Hash256 storageRoot = CalculateStorageRoot(address, slot, value);
        StateTree expectedState = new();
        expectedState.Set(address, account.WithChangedStorageRoot(storageRoot));
        expectedState.UpdateRootHash();

        sink.OnCommittedAccount(address, account);
        if (sparseStorage)
        {
            StorageCell cell = new(address, in slot);
            sink.OnCommittedStorage(in cell, value);
        }
        sink.OnCommitPhaseCompleted(isFinal: false);
        sink.OnCommitPhaseCompleted(isFinal: true);

        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            using (IWorldStateScopeProvider.IStorageWriteBatch storageBatch =
                   writeBatch.CreateStorageWriteBatch(address, 1))
            {
                storageBatch.Clear();
                storageBatch.Set(in slot, value);
            }
            writeBatch.Set(address, account);
        }

        scope.UpdateRootHash();
        Assert.That(scope.RootHash, Is.EqualTo(expectedState.RootHash), "sparse root");

        scope.UpdateRootHash();
        Assert.That(scope.RootHash, Is.EqualTo(expectedState.RootHash), "Patricia fallback root");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SparseWriteBatch_ConcurrentStorageCallbacksMatchPatricia(bool sparseStorage)
    {
        using TestContext ctx = new(
            new FlatDbConfig
            {
                UseSparseRootComputation = true,
                UseSparseStorageRootComputation = sparseStorage,
            },
            new NoopTrieWarmer());
        FlatWorldStateScope scope = ctx.Scope;
        IWorldStateScopeProvider.ISparseDeltaSink sink = scope;

        Address firstAddress = TestItem.AddressA;
        Address secondAddress = TestItem.AddressB;
        Account firstAccount = new(1, (UInt256)1_000);
        Account secondAccount = new(2, (UInt256)2_000);
        UInt256 firstSlot = 1;
        UInt256 secondSlot = 2;
        byte[] firstValue = [0x12];
        byte[] secondValue = [0x34];

        sink.OnCommittedAccount(firstAddress, firstAccount);
        sink.OnCommittedAccount(secondAddress, secondAccount);
        if (sparseStorage)
        {
            StorageCell firstCell = new(firstAddress, in firstSlot);
            StorageCell secondCell = new(secondAddress, in secondSlot);
            sink.OnCommittedStorage(in firstCell, firstValue);
            sink.OnCommittedStorage(in secondCell, secondValue);
        }
        sink.OnCommitPhaseCompleted(isFinal: true);

        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(2))
        {
            IWorldStateScopeProvider.IStorageWriteBatch firstStorage =
                writeBatch.CreateStorageWriteBatch(firstAddress, 1);
            IWorldStateScopeProvider.IStorageWriteBatch secondStorage =
                writeBatch.CreateStorageWriteBatch(secondAddress, 1);
            firstStorage.Set(in firstSlot, firstValue);
            secondStorage.Set(in secondSlot, secondValue);

            await Task.WhenAll(
                Task.Run(firstStorage.Dispose),
                Task.Run(secondStorage.Dispose));

            writeBatch.Set(firstAddress, firstAccount);
            writeBatch.Set(secondAddress, secondAccount);
        }

        StateTree expectedState = new();
        expectedState.Set(firstAddress, firstAccount.WithChangedStorageRoot(
            CalculateStorageRoot(firstAddress, firstSlot, firstValue)));
        expectedState.Set(secondAddress, secondAccount.WithChangedStorageRoot(
            CalculateStorageRoot(secondAddress, secondSlot, secondValue)));
        expectedState.UpdateRootHash();

        scope.UpdateRootHash();
        Assert.That(scope.RootHash, Is.EqualTo(expectedState.RootHash));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SparseWriteBatch_DoesNotResolveNewStorageRootBeforeSnapshotCommit(bool sparseStorage)
    {
        using TestContext ctx = new(
            new FlatDbConfig
            {
                UseSparseRootComputation = true,
                UseSparseStorageRootComputation = sparseStorage,
            },
            new NoopTrieWarmer());

        Address address = TestItem.AddressA;
        Hash256 addressHash = address.ToAccountPath.ToCommitment();
        UInt256 slot = 1;
        byte[] parentValue = [0x12];
        byte[] newValue = [0x34];

        RawTrieStore store = new(new MemDb());
        StorageTree referenceStorage = new(store.GetTrieStore(addressHash), LimboLogs.Instance);
        referenceStorage.Set(in slot, parentValue);
        referenceStorage.UpdateRootHash();
        Hash256 parentStorageRoot = referenceStorage.RootHash;
        TrieNode parentRootNode = new(
            NodeType.Unknown,
            parentStorageRoot,
            referenceStorage.RootRef!.FullRlp);
        referenceStorage.Commit();
        Account parentAccount = new(1, (UInt256)1_000, parentStorageRoot, Keccak.OfAnEmptyString);

        ctx.AddSnapshot(content =>
        {
            content.Accounts[address] = parentAccount;
            content.Storages[(address, slot)] = SlotValue.FromSpanWithoutLeadingZero(parentValue);
            content.StorageNodes[(addressHash, TreePath.Empty)] = parentRootNode;
        });

        referenceStorage.Set(in slot, newValue);
        referenceStorage.UpdateRootHash();
        Hash256 expectedStorageRoot = referenceStorage.RootHash;
        StateTree expectedState = new();
        expectedState.Set(address, parentAccount.WithChangedStorageRoot(expectedStorageRoot));
        expectedState.UpdateRootHash();

        FlatWorldStateScope scope = ctx.Scope;
        IWorldStateScopeProvider.ISparseDeltaSink sink = scope;
        if (sparseStorage)
        {
            StorageCell cell = new(address, in slot);
            sink.OnCommittedStorage(in cell, newValue);
        }
        sink.OnCommitPhaseCompleted(isFinal: true);

        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            using (IWorldStateScopeProvider.IStorageWriteBatch storageBatch =
                   writeBatch.CreateStorageWriteBatch(address, 1))
                storageBatch.Set(in slot, newValue);
            writeBatch.Set(address, parentAccount);
        }

        scope.UpdateRootHash();

        Assert.That(scope.RootHash, Is.EqualTo(expectedState.RootHash));
    }

    [Test]
    public void SparseAcceptedRoot_IsReusedByNextBlock()
    {
        using TestContext ctx = new(
            new FlatDbConfig { UseSparseRootComputation = true },
            new NoopTrieWarmer());
        FlatWorldStateScope scope = ctx.Scope;
        IWorldStateScopeProvider.ISparseDeltaSink sink = scope;
        StateTree expectedState = new();

        Address firstAddress = TestItem.AddressA;
        Account firstAccount = new(1, (UInt256)1_000);
        sink.OnCommittedAccount(firstAddress, firstAccount);
        sink.OnCommitPhaseCompleted(isFinal: true);
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            writeBatch.Set(firstAddress, firstAccount);

        expectedState.Set(firstAddress, firstAccount);
        expectedState.UpdateRootHash();
        scope.UpdateRootHash();
        Assert.That(scope.RootHash, Is.EqualTo(expectedState.RootHash));
        scope.Commit(1);

        Address secondAddress = TestItem.AddressB;
        Account secondAccount = new(2, (UInt256)2_000);
        sink.OnCommittedAccount(secondAddress, secondAccount);
        sink.OnCommitPhaseCompleted(isFinal: true);
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            writeBatch.Set(secondAddress, secondAccount);

        expectedState.Set(secondAddress, secondAccount);
        expectedState.UpdateRootHash();
        scope.UpdateRootHash();
        Assert.That(scope.RootHash, Is.EqualTo(expectedState.RootHash));
    }

    [Test]
    public void SparseScope_WarmerIgnoresSnapshotRootWithDifferentHash()
    {
        Hash256 parentRoot = TestItem.KeccakA;
        Hash256 staleRoot = TestItem.KeccakB;
        using TestContext ctx = new(
            new FlatDbConfig { UseSparseRootComputation = true },
            new NoopTrieWarmer(),
            new StateId(1, parentRoot.ValueHash256));
        ctx.AddSnapshot(content =>
            content.StateNodes[TreePath.Empty] = new TrieNode(NodeType.Unknown, staleRoot));

        Assert.That(() => _ = ctx.Scope, Throws.Nothing);
    }

    [Test]
    public void SparseScope_MaterializedSnapshotRootWinsOverUnresolvedGlobalCache()
    {
        Address address = TestItem.AddressA;
        Account account = new(1, (UInt256)1_000);
        StateTree expectedState = new();
        expectedState.Set(address, account);
        expectedState.UpdateRootHash();
        Hash256 root = expectedState.RootHash;
        TrieNode materializedRoot = new(NodeType.Unknown, root, expectedState.RootRef!.FullRlp);

        using TestContext ctx = new(
            new FlatDbConfig { UseSparseRootComputation = true, VerifyWithTrie = true },
            new NoopTrieWarmer(),
            new StateId(1, root.ValueHash256));
        ctx.AddSnapshot(content =>
        {
            content.Accounts[address] = account;
            content.StateNodes[TreePath.Empty] = materializedRoot;
        });

        TransientResource cacheEntries = ctx.ResourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        cacheEntries.UpdateStateNode(TreePath.Empty, new TrieNode(NodeType.Unknown, root));
        ctx.TrieNodeCache.Add(cacheEntries);
        ctx.ResourcePool.ReturnCachedResource(ResourcePool.Usage.MainBlockProcessing, cacheEntries);
        Assert.That(
            ctx.TrieNodeCache.TryGet(null, TreePath.Empty, root, out TrieNode? cachedRoot),
            Is.True,
            "unresolved root must be present in the global cache");
        Assert.That(cachedRoot!.HasRlp, Is.False);

        FlatWorldStateScope scope = ctx.Scope;
        Assert.That(cachedRoot.HasRlp, Is.False, "scope construction must not materialize the cache placeholder");
        Account? actual = scope.Get(address);
        Assert.That(cachedRoot.HasRlp, Is.False, "the main state tree must use the materialized snapshot node");
        scope.IncrementOutstandingWarmups();
        bool warmed = scope.WarmUpStateTrie(address, scope.HintSequenceId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(account));
            Assert.That(warmed, Is.True);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SparseWorldState_MultiPhaseStorageAcrossBlocks_MatchesPatricia(bool sparseStorage)
    {
        using TestContext ctx = new(
            new FlatDbConfig
            {
                UseSparseRootComputation = true,
                UseSparseStorageRootComputation = sparseStorage,
                VerifyWithTrie = true,
            },
            new NoopTrieWarmer());
        FlatWorldStateScope scope = ctx.Scope;
        WorldState worldState = new(new ExistingScopeProvider(scope), LimboLogs.Instance);
        using IDisposable worldStateScope = worldState.BeginScope(baseBlock: null);

        Address firstAddress = TestItem.AddressA;
        Address secondAddress = TestItem.AddressB;
        Address thirdAddress = TestItem.AddressC;
        UInt256 firstSlot = 1;
        UInt256 secondSlot = 2;
        UInt256 thirdSlot = 4;
        StorageCell firstCell = new(firstAddress, in firstSlot);
        StorageCell secondCell = new(secondAddress, in secondSlot);
        StorageCell thirdCell = new(thirdAddress, in thirdSlot);
        byte[] firstInitialValue = [0x11];
        byte[] firstFinalValue = [0x33];
        byte[] secondValue = [0x22];
        byte[] thirdInitialValue = [0x55];
        byte[] thirdFinalValue = [0x66];

        RawTrieStore referenceStore = new(new MemDb());
        StorageTree firstStorage = new(
            referenceStore.GetTrieStore(firstAddress.ToAccountPath.ToCommitment()),
            LimboLogs.Instance);
        StorageTree secondStorage = new(
            referenceStore.GetTrieStore(secondAddress.ToAccountPath.ToCommitment()),
            LimboLogs.Instance);
        StorageTree thirdStorage = new(
            referenceStore.GetTrieStore(thirdAddress.ToAccountPath.ToCommitment()),
            LimboLogs.Instance);
        StateTree expectedState = new();

        worldState.CreateAccount(firstAddress, (UInt256)1_000);
        worldState.CreateAccount(secondAddress, (UInt256)2_000);
        worldState.CreateAccount(thirdAddress, (UInt256)3_000);
        worldState.Set(in firstCell, firstInitialValue);
        worldState.Set(in secondCell, secondValue);
        worldState.Set(in thirdCell, thirdInitialValue);
        worldState.Commit(
            Nethermind.Specs.Forks.Cancun.Instance,
            Nethermind.Evm.Tracing.State.NullStateTracer.Instance,
            commitRoots: false);

        worldState.SetNonce(firstAddress, 1);
        worldState.AddToBalance(secondAddress, (UInt256)100, Nethermind.Specs.Forks.Cancun.Instance);
        worldState.Set(in firstCell, firstFinalValue);
        worldState.Set(in thirdCell, thirdFinalValue);
        worldState.Commit(
            Nethermind.Specs.Forks.Cancun.Instance,
            Nethermind.Evm.Tracing.State.NullStateTracer.Instance,
            commitRoots: false);
        worldState.Commit(
            Nethermind.Specs.Forks.Cancun.Instance,
            Nethermind.Evm.Tracing.State.NullStateTracer.Instance,
            commitRoots: true);
        worldState.RecalculateStateRoot();

        firstStorage.Set(in firstSlot, firstFinalValue);
        firstStorage.UpdateRootHash();
        secondStorage.Set(in secondSlot, secondValue);
        secondStorage.UpdateRootHash();
        thirdStorage.Set(in thirdSlot, thirdFinalValue);
        thirdStorage.UpdateRootHash();
        expectedState.Set(firstAddress, new Account(1, (UInt256)1_000, firstStorage.RootHash, Keccak.OfAnEmptyString));
        expectedState.Set(secondAddress, new Account(0, (UInt256)2_100, secondStorage.RootHash, Keccak.OfAnEmptyString));
        expectedState.Set(thirdAddress, new Account(0, (UInt256)3_000, thirdStorage.RootHash, Keccak.OfAnEmptyString));
        expectedState.UpdateRootHash();
        Assert.That(worldState.StateRoot, Is.EqualTo(expectedState.RootHash), "block one");

        worldState.CommitTree(1);
        Assert.That(ctx.LastCommittedSnapshot, Is.Not.Null);
        AssertStorageRootPersisted(ctx.LastCommittedSnapshot!, firstAddress, firstStorage.RootHash);
        AssertStorageRootPersisted(ctx.LastCommittedSnapshot!, secondAddress, secondStorage.RootHash);
        AssertStorageRootPersisted(ctx.LastCommittedSnapshot!, thirdAddress, thirdStorage.RootHash);
        worldState.Reset();

        scope.IncrementOutstandingWarmups();
        Assert.That(scope.WarmUpStateTrie(firstAddress, scope.HintSequenceId), Is.True);

        UInt256 nextSlot = 3;
        StorageCell nextCell = new(firstAddress, in nextSlot);
        byte[] nextValue = [0x44];
        byte[] thirdNextValue = [0x77];
        worldState.Set(in nextCell, nextValue);
        worldState.Set(in secondCell, StorageTree.ZeroBytes);
        worldState.Set(in thirdCell, thirdNextValue);
        worldState.SetNonce(secondAddress, 2);
        worldState.AddToBalance(firstAddress, (UInt256)50, Nethermind.Specs.Forks.Cancun.Instance);
        worldState.Commit(
            Nethermind.Specs.Forks.Cancun.Instance,
            Nethermind.Evm.Tracing.State.NullStateTracer.Instance,
            commitRoots: false);
        worldState.Commit(
            Nethermind.Specs.Forks.Cancun.Instance,
            Nethermind.Evm.Tracing.State.NullStateTracer.Instance,
            commitRoots: true);
        worldState.RecalculateStateRoot();

        firstStorage.Set(in nextSlot, nextValue);
        firstStorage.UpdateRootHash();
        secondStorage.Set(in secondSlot, StorageTree.ZeroBytes);
        secondStorage.UpdateRootHash();
        thirdStorage.Set(in thirdSlot, thirdNextValue);
        thirdStorage.UpdateRootHash();
        expectedState.Set(firstAddress, new Account(1, (UInt256)1_050, firstStorage.RootHash, Keccak.OfAnEmptyString));
        expectedState.Set(secondAddress, new Account(2, (UInt256)2_100, secondStorage.RootHash, Keccak.OfAnEmptyString));
        expectedState.Set(thirdAddress, new Account(0, (UInt256)3_000, thirdStorage.RootHash, Keccak.OfAnEmptyString));
        expectedState.UpdateRootHash();
        Assert.That(worldState.StateRoot, Is.EqualTo(expectedState.RootHash), "block two");
    }

    private static void AssertStorageRootPersisted(Snapshot snapshot, Address address, Hash256 expectedRoot)
    {
        HashedKey<(Hash256, TreePath)> key = new((address.ToAccountPath.ToCommitment(), TreePath.Empty));
        Assert.That(snapshot.TryGetStorageNode(key, out TrieNode? node), Is.True, address.ToString());
        Assert.That(node!.Keccak, Is.EqualTo(expectedRoot), address.ToString());
    }

    private static Hash256 CalculateStorageRoot(Address address, in UInt256 slot, byte[] value)
    {
        RawTrieStore store = new(new MemDb());
        StorageTree storageTree = new(
            store.GetTrieStore(address.ToAccountPath.ToCommitment()),
            LimboLogs.Instance);
        storageTree.Set(in slot, value);
        storageTree.UpdateRootHash();
        return storageTree.RootHash;
    }

    private sealed class ExistingScopeProvider(FlatWorldStateScope scope) : IWorldStateScopeProvider
    {
        public bool HasRoot(BlockHeader? baseBlock) => true;

        public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock, LocalMetrics metrics) => scope;
    }

    private sealed class RecordingTrieWarmer(bool acceptSlotJob, bool acceptMpmcSlotJob) : ITrieWarmer
    {
        public int SlotJobPushes { get; private set; }
        public int MpmcSlotJobPushes { get; private set; }

        public bool PushSlotJob(ITrieWarmer.IStorageWarmer storageTree, in UInt256 index, int sequenceId)
        {
            SlotJobPushes++;
            return acceptSlotJob;
        }

        public bool PushSlotJobMpmc(ITrieWarmer.IStorageWarmer storageTree, in UInt256 index, int sequenceId)
        {
            MpmcSlotJobPushes++;
            return acceptMpmcSlotJob;
        }

        public bool PushAddressJob(ITrieWarmer.IAddressWarmer scope, Address? path, int sequenceId) => false;

        public void OnEnterScope() { }

        public void OnExitScope() { }
    }

    private sealed class BlockingPersistenceReader : IPersistence.IPersistenceReader
    {
        private int _activeReads;
        private volatile bool _isDisposed;
        private volatile bool _disposedDuringActiveRead;

        public ManualResetEventSlim ReadEntered { get; } = new(false);
        public ManualResetEventSlim ResumeReads { get; } = new(false);
        public bool IsDisposed => _isDisposed;
        public bool DisposedDuringActiveRead => _disposedDuringActiveRead;

        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags)
        {
            Interlocked.Increment(ref _activeReads);
            try
            {
                ReadEntered.Set();
                ResumeReads.Wait(TimeSpan.FromSeconds(60));
                return null;
            }
            finally
            {
                Interlocked.Decrement(ref _activeReads);
            }
        }

        public void Dispose()
        {
            if (Volatile.Read(ref _activeReads) != 0) _disposedDuringActiveRead = true;
            _isDisposed = true;
            ReadEntered.Dispose();
            ResumeReads.Dispose();
        }

        public Account? GetAccount(Address address) => null;
        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue) => false;
        public StateId CurrentState => new(0, Keccak.EmptyTreeHash);
        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => null;
        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => null;
        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref SlotValue value) => false;
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => throw new NotSupportedException();
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => throw new NotSupportedException();
        public bool IsPreimageMode => false;
    }
}
