// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Autofac;
using Nethermind.Blockchain.Synchronization;
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
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private IContainer? _container;
        private IContainer Container => _container ??= _containerBuilder.Build();

        public ResourcePool ResourcePool => field ??= Container.Resolve<ResourcePool>();
        public SnapshotPooledList ReadOnlySnapshots = new SnapshotPooledList(0);
        public IPersistence.IPersistenceReader PersistenceReader => field ??= Container.Resolve<IPersistence.IPersistenceReader>();
        public Snapshot? LastCommittedSnapshot { get; set; }
        public TransientResource? LastCreatedCachedResource { get; set; }

        public TestContext(FlatDbConfig? config = null)
        {
            config ??= new FlatDbConfig();

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
                ;

            // Externally owned because snapshot bundle take ownership
            _containerBuilder.RegisterType<ReadOnlySnapshotBundle>()
                .WithParameter(TypedParameter.From(false)) // recordDetailedMetrics
                .WithParameter(TypedParameter.From(ReadOnlySnapshots))
                .ExternallyOwned();

            ConfigureSnapshotBundle();
            ConfigureFlatWorldStateScope();
        }

        private void ConfigureSnapshotBundle()
        {
            _containerBuilder.RegisterType<SnapshotBundle>()
                .SingleInstance()
                .WithParameter(TypedParameter.From(ResourcePool.Usage.MainBlockProcessing))
                .ExternallyOwned();
            ;
        }

        private void ConfigureFlatWorldStateScope()
        {
            _containerBuilder.RegisterType<FlatWorldStateScope>()
                .SingleInstance()
                .WithParameter(TypedParameter.From(new StateId(0, Keccak.EmptyTreeHash)))
                ;
        }

        public FlatWorldStateScope Scope => Container.Resolve<FlatWorldStateScope>();

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();

            LastCommittedSnapshot?.Dispose();
            if (LastCreatedCachedResource is not null) ResourcePool.ReturnCachedResource(ResourcePool.Usage.MainBlockProcessing, LastCreatedCachedResource);

            _container?.Dispose();
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
        using TestContext ctx = new TestContext();

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
        using TestContext ctx = new TestContext();

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
        using TestContext ctx = new TestContext();
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
        using TestContext ctx = new TestContext();
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

        ctx.LastCommittedSnapshot!.TryGetStorage(testAddress, slotIndex, out SlotValue? committedSlot);
        Assert.That(committedSlot!.Value.ToEvmBytes(), Is.EqualTo(slotValue));
    }

    #endregion

    #region Selfdestruct Interaction Tests

    [Test]
    public void TestSelfDestructBlocksEarlierAccountAndSlot()
    {
        using TestContext ctx = new TestContext();
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
        using TestContext ctx = new TestContext();
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
        using TestContext ctx = new TestContext();
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
        TestMemDb testDb = new TestMemDb();
        RawScopedTrieStore trieStore = new RawScopedTrieStore(testDb);
        StorageTree expectedTree = new StorageTree(trieStore, LimboLogs.Instance);
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
        using TestContext ctx = new TestContext();
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
        TestMemDb testDb = new TestMemDb();
        RawScopedTrieStore trieStore = new RawScopedTrieStore(testDb);
        StorageTree expectedTree = new StorageTree(trieStore, LimboLogs.Instance);
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
        using TestContext ctx = new TestContext();
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
        TestMemDb testDb = new TestMemDb();
        RawScopedTrieStore trieStore = new RawScopedTrieStore(testDb);
        StorageTree expectedTree = new StorageTree(trieStore, LimboLogs.Instance);
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
        using TestContext ctx = new TestContext();
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
        TestMemDb testDb = new TestMemDb();
        RawScopedTrieStore trieStore = new RawScopedTrieStore(testDb);
        StorageTree expectedTree = new StorageTree(trieStore, LimboLogs.Instance);
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
        using TestContext ctx = new TestContext();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;

        Account initialAccount = new Account(0, 0);
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
        using TestContext ctx = new TestContext();
        FlatWorldStateScope scope = ctx.Scope;

        Address addr1 = TestItem.AddressA;
        Address addr2 = TestItem.AddressB;
        Account acc1 = new Account(100, 1000);
        Account acc2 = new Account(200, 2000);
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

        ctx.LastCommittedSnapshot!.TryGetStorage(addr1, slot1, out SlotValue? committedSlot);
        Assert.That(committedSlot!.Value.ToEvmBytes(), Is.EqualTo(val1));
    }

    [Test]
    public void TestMultipleCommitsAccumulateData()
    {
        using TestContext ctx = new TestContext();
        FlatWorldStateScope scope = ctx.Scope;

        Address addr1 = TestItem.AddressA;
        Address addr2 = TestItem.AddressB;
        Account acc1 = new Account(100, 1000);
        Account acc2 = new Account(200, 2000);

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
        using TestContext ctx = new TestContext();
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

        using TestContext ctx = new TestContext();
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
            TrieNode storageNode = new TrieNode(NodeType.Leaf, Keccak.Zero);
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

        using TestContext ctx = new TestContext();
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

    #endregion

}
