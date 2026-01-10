// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Autofac;
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

    private class TestContext: IDisposable
    {
        private readonly ContainerBuilder _containerBuilder;
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private IContainer? _container;
        private IContainer Container => _container ??= _containerBuilder.Build();

        public ResourcePool ResourcePool => field ??= Container.Resolve<ResourcePool>();
        public ArrayPoolList<Snapshot> ReadOnlySnapshots => field ??= Container.Resolve<ArrayPoolList<Snapshot>>();
        public IPersistence.IPersistenceReader PersistenceReader => field ??= Container.Resolve<IPersistence.IPersistenceReader>();
        public Snapshot? LastCommittedSnapshot { get; set; }
        public CachedResource? LastCreatedCachedResource { get; set; }

        public TestContext(FlatDbConfig? config = null)
        {
            config ??= new FlatDbConfig();

            _containerBuilder = new ContainerBuilder()
                    .AddModule(new FlatWorldStateModule(config))
                    .AddSingleton<IPersistence.IPersistenceReader>(_ => Substitute.For<IPersistence.IPersistenceReader>())
                    .AddSingleton<IFlatDiffRepository>((ctx) =>
                    {
                        ResourcePool resourcePool = ctx.Resolve<ResourcePool>();
                        IFlatDiffRepository flatDiff = Substitute.For<IFlatDiffRepository>();
                        flatDiff.When(it => it.AddSnapshot(Arg.Any<Snapshot>(), Arg.Any<CachedResource>()))
                            .Do(c =>
                            {
                                Snapshot snapshot = (Snapshot)c[0];
                                CachedResource cachedResource = (CachedResource)c[1];

                                if (LastCommittedSnapshot is not null)
                                {
                                    LastCommittedSnapshot.Dispose();
                                }
                                LastCommittedSnapshot = snapshot;

                                if (LastCreatedCachedResource is not null)
                                {
                                    resourcePool.ReturnCachedResource(IFlatDiffRepository.SnapshotBundleUsage.MainBlockProcessing, cachedResource);
                                }
                                LastCreatedCachedResource = cachedResource;
                            });

                        return flatDiff;
                    })
                    .AddSingleton<IProcessExitSource>(_ => new CancellationTokenSourceProcessExitSource(_cancellationTokenSource))
                    .AddSingleton<ILogManager>(LimboLogs.Instance)
                    .AddSingleton<IFlatDbConfig>(config)
                    .AddSingleton<ArrayPoolList<Snapshot>>((_) => new ArrayPoolList<Snapshot>(1))
                    .AddSingleton<IWorldStateScopeProvider.ICodeDb>(_ => new TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb(new TestMemDb()))
                ;

            // Externally owned because snapshot bundle take ownership
            _containerBuilder.RegisterType<ReadOnlySnapshotBundle>().ExternallyOwned();

            ConfigureSnapshotBundle();
            ConfigureFlatWorldStateScope();
        }

        private void ConfigureSnapshotBundle()
        {
            _containerBuilder.RegisterType<SnapshotBundle>()
                .SingleInstance()
                .WithParameter(TypedParameter.From(IFlatDiffRepository.SnapshotBundleUsage.MainBlockProcessing))
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
            if (LastCreatedCachedResource is not null) ResourcePool.ReturnCachedResource(IFlatDiffRepository.SnapshotBundleUsage.MainBlockProcessing, LastCreatedCachedResource);

            _container?.Dispose();
            _cancellationTokenSource.Dispose();
        }

        public class CancellationTokenSourceProcessExitSource(CancellationTokenSource cancellationTokenSource) : IProcessExitSource
        {
            public CancellationToken Token => cancellationTokenSource.Token;

            public void Exit(int exitCode)
            {
                throw new NotImplementedException();
            }
        }

        public void AddSnapshot(Action<SnapshotContent> populator)
        {
            SnapshotContent snapshotContent = ResourcePool.GetSnapshotContent(IFlatDiffRepository.SnapshotBundleUsage.MainBlockProcessing);
            populator(snapshotContent);

            ReadOnlySnapshots.Add(new Snapshot(
                StateId.PreGenesis,
                StateId.PreGenesis,
                snapshotContent,
                ResourcePool,
                IFlatDiffRepository.SnapshotBundleUsage.MainBlockProcessing));
        }
    }


    [Test]
    public void TestGetAccountFromReadonlySnapshotWillReturnLastAccount()
    {
        using TestContext ctx = new TestContext();

        Address testAddress = TestItem.AddressA;
        Account persistenceAccount = TestItem.GenerateRandomAccount();
        Account olderAccount = TestItem.GenerateRandomAccount();
        Account testAccount = TestItem.GenerateRandomAccount();

        ctx.PersistenceReader.GetAccount(testAddress).Returns(persistenceAccount);
        ctx.AddSnapshot(content => content.Accounts[testAddress] = olderAccount);
        ctx.AddSnapshot(content => content.Accounts[testAddress] = testAccount);

        Assert.That(ctx.Scope.Get(testAddress), Is.EqualTo(testAccount));
    }

    [Test]
    public void TestGetAccountFromPersistenceReader()
    {
        using TestContext ctx = new TestContext();

        Address testAddress = TestItem.AddressA;
        Account testAccount = TestItem.GenerateRandomAccount();

        ctx.PersistenceReader.GetAccount(testAddress).Returns(testAccount);

        Assert.That(ctx.Scope.Get(testAddress), Is.EqualTo(testAccount));
    }

    [Test]
    public void TestGetAccountFromWrittenAccount()
    {
        using TestContext ctx = new TestContext();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        Account testAccount = TestItem.GenerateRandomAccount();
        Account persistenceAccount = TestItem.GenerateRandomAccount();
        Account olderAccount = TestItem.GenerateRandomAccount();

        ctx.PersistenceReader.GetAccount(testAddress).Returns(persistenceAccount);
        ctx.AddSnapshot(content => content.Accounts[testAddress] = olderAccount);

        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            writeBatch.Set(testAddress, testAccount);
        }

        Assert.That(ctx.Scope.Get(testAddress), Is.EqualTo(testAccount));
    }


    [Test]
    public void TestGetAccountAfterCommit()
    {
        using TestContext ctx = new TestContext();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        Account testAccount = TestItem.GenerateRandomAccount();
        Account persistenceAccount = TestItem.GenerateRandomAccount();
        Account olderAccount = TestItem.GenerateRandomAccount();

        ctx.PersistenceReader.GetAccount(testAddress).Returns(persistenceAccount);
        ctx.AddSnapshot(content => content.Accounts[testAddress] = olderAccount);

        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            writeBatch.Set(testAddress, testAccount);
        }

        // After commit check
        scope.Commit(1);

        Assert.That(ctx.Scope.Get(testAddress), Is.EqualTo(testAccount));
        ctx.LastCommittedSnapshot!.TryGetAccount(testAddress, out Account? committedAccount);
        Assert.That(committedAccount, Is.EqualTo(testAccount));
    }

    // ===== SLOT TESTS =====

    [Test]
    public void TestGetSlotFromFirstSnapshot()
    {
        using TestContext ctx = new TestContext();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        UInt256 slotIndex = 1;
        byte[] slotValue = { 0x01, 0x02, 0x03 };

        Account account = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(testAddress).Returns(account);

        // Add slot to first (and only) readonly snapshot
        ctx.AddSnapshot(content => content.Storages[(testAddress, slotIndex)] = SlotValue.FromSpanWithoutLeadingZero(slotValue));

        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(testAddress);
        byte[] result = storageTree.Get(slotIndex);

        Assert.That(result, Is.EqualTo(slotValue));
    }

    [Test]
    public void TestGetSlotFromMiddleSnapshot()
    {
        using TestContext ctx = new TestContext();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        UInt256 slotIndex = 1;
        byte[] olderSlotValue = { 0x01, 0x02 };
        byte[] middleSlotValue = { 0x03, 0x04, 0x05 };
        byte[] newerSlotValue = { 0x06, 0x07, 0x08, 0x09 };

        Account account = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(testAddress).Returns(account);

        // Add slots to multiple snapshots - newer should shadow older
        ctx.AddSnapshot(content => content.Storages[(testAddress, slotIndex)] = SlotValue.FromSpanWithoutLeadingZero(olderSlotValue));
        ctx.AddSnapshot(content => content.Storages[(testAddress, slotIndex)] = SlotValue.FromSpanWithoutLeadingZero(middleSlotValue));
        ctx.AddSnapshot(content => content.Storages[(testAddress, slotIndex)] = SlotValue.FromSpanWithoutLeadingZero(newerSlotValue));

        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(testAddress);
        byte[] result = storageTree.Get(slotIndex);

        // Should return the newest value
        Assert.That(result, Is.EqualTo(newerSlotValue));
    }

    [Test]
    public void TestGetSlotFromLastSnapshot()
    {
        using TestContext ctx = new TestContext();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        UInt256 slotIndex = 1;
        byte[] slotValue = { 0xAA, 0xBB, 0xCC };

        Account account = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(testAddress).Returns(account);

        // Add empty first snapshots, then add slot to last snapshot
        ctx.AddSnapshot(content => { });
        ctx.AddSnapshot(content => { });
        ctx.AddSnapshot(content => content.Storages[(testAddress, slotIndex)] = SlotValue.FromSpanWithoutLeadingZero(slotValue));

        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(testAddress);
        byte[] result = storageTree.Get(slotIndex);

        Assert.That(result, Is.EqualTo(slotValue));
    }

    [Test]
    public void TestGetSlotFromPersistence()
    {
        using TestContext ctx = new TestContext();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        UInt256 slotIndex = 1;
        byte[] persistedSlotValue = { 0xDE, 0xAD, 0xBE, 0xEF };

        Account account = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(testAddress).Returns(account);

        SlotValue outValue = SlotValue.FromSpanWithoutLeadingZero(persistedSlotValue)!.Value;
        ctx.PersistenceReader.TryGetSlot(testAddress, slotIndex, ref Arg.Any<SlotValue>())
            .Returns(x => {
                x[2] = outValue;
                return true;
            });

        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(testAddress);
        byte[] result = storageTree.Get(slotIndex);

        Assert.That(result, Is.EqualTo(persistedSlotValue));
    }

    [Test]
    public void TestGetSlotFromWrittenSlot()
    {
        using TestContext ctx = new TestContext();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        UInt256 slotIndex = 1;
        byte[] olderSlotValue = { 0x01, 0x02 };
        byte[] writtenSlotValue = { 0xFF, 0xFF };

        Account account = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(testAddress).Returns(account);

        // Add slot to snapshot
        ctx.AddSnapshot(content => content.Storages[(testAddress, slotIndex)] = SlotValue.FromSpanWithoutLeadingZero(olderSlotValue));

        // Write new value using write batch
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(testAddress, 1);
            storageBatch.Set(slotIndex, writtenSlotValue);
            storageBatch.Dispose();
        }

        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(testAddress);
        byte[] result = storageTree.Get(slotIndex);

        // Written value should take precedence
        Assert.That(result, Is.EqualTo(writtenSlotValue));
    }

    // ===== SELFDESTRUCT INTERACTION TESTS =====

    [Test]
    public void TestGetSlotAfterSelfDestructReturnsNull()
    {
        using TestContext ctx = new TestContext();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        UInt256 slotIndex = 1;
        byte[] oldSlotValue = { 0x01, 0x02, 0x03 };

        Account account = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(testAddress).Returns(account);

        // Add slot in first snapshot
        ctx.AddSnapshot(content => content.Storages[(testAddress, slotIndex)] = SlotValue.FromSpanWithoutLeadingZero(oldSlotValue));

        // Add selfdestruct in second snapshot (isNewAccount = false means there was storage to clear)
        ctx.AddSnapshot(content => content.SelfDestructedStorageAddresses[testAddress] = false);

        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(testAddress);
        byte[] result = storageTree.Get(slotIndex);

        // Should return zero bytes after selfdestruct
        Assert.That(result, Is.EqualTo(StorageTree.ZeroBytes));
    }

    [Test]
    public void TestGetSlotWithSelfDestructInMiddleSnapshot()
    {
        using TestContext ctx = new TestContext();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        UInt256 slotIndex = 1;
        byte[] oldSlotValue = { 0x01, 0x02 };
        byte[] persistedSlotValue = { 0xAA, 0xBB };

        Account account = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(testAddress).Returns(account);

        SlotValue outValue = SlotValue.FromSpanWithoutLeadingZero(persistedSlotValue)!.Value;
        ctx.PersistenceReader.TryGetSlot(testAddress, slotIndex, ref Arg.Any<SlotValue>())
            .Returns(x => {
                x[2] = outValue;
                return true;
            });

        // Oldest snapshot has slot value
        ctx.AddSnapshot(content => content.Storages[(testAddress, slotIndex)] = SlotValue.FromSpanWithoutLeadingZero(oldSlotValue));

        // Middle snapshot has selfdestruct - this blocks reading from earlier snapshots
        ctx.AddSnapshot(content => content.SelfDestructedStorageAddresses[testAddress] = false);

        // Newer snapshot (empty)
        ctx.AddSnapshot(content => { });

        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(testAddress);
        byte[] result = storageTree.Get(slotIndex);

        // Should return zero bytes because selfdestruct blocks reading earlier values
        // and no new value was written after selfdestruct
        Assert.That(result, Is.EqualTo(StorageTree.ZeroBytes));
    }

    [Test]
    public void TestGetSlotWithSelfDestructAndNewValue()
    {
        using TestContext ctx = new TestContext();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        UInt256 slotIndex = 1;
        byte[] oldSlotValue = { 0x01, 0x02 };
        byte[] newSlotValue = { 0xFF, 0xFF };

        Account account = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(testAddress).Returns(account);

        // Oldest snapshot has slot value
        ctx.AddSnapshot(content => content.Storages[(testAddress, slotIndex)] = SlotValue.FromSpanWithoutLeadingZero(oldSlotValue));

        // Middle snapshot has selfdestruct
        ctx.AddSnapshot(content => content.SelfDestructedStorageAddresses[testAddress] = false);

        // Newest snapshot has new slot value written after selfdestruct
        ctx.AddSnapshot(content => content.Storages[(testAddress, slotIndex)] = SlotValue.FromSpanWithoutLeadingZero(newSlotValue));

        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(testAddress);
        byte[] result = storageTree.Get(slotIndex);

        // Should return the new value written after selfdestruct
        Assert.That(result, Is.EqualTo(newSlotValue));
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

        Account account = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(testAddress).Returns(account);

        // Snapshot 0: slot1 exists
        ctx.AddSnapshot(content => content.Storages[(testAddress, slot1)] = SlotValue.FromSpanWithoutLeadingZero(slot1BeforeValue));

        // Snapshot 1: selfdestruct happens at this index
        ctx.AddSnapshot(content => content.SelfDestructedStorageAddresses[testAddress] = false);

        // Snapshot 2: slot2 is set after selfdestruct
        ctx.AddSnapshot(content => content.Storages[(testAddress, slot2)] = SlotValue.FromSpanWithoutLeadingZero(slot2AfterValue));

        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(testAddress);

        // slot1 should return zero (blocked by selfdestruct)
        byte[] result1 = storageTree.Get(slot1);
        Assert.That(result1, Is.EqualTo(StorageTree.ZeroBytes));

        // slot2 should return the value (written after selfdestruct)
        byte[] result2 = storageTree.Get(slot2);
        Assert.That(result2, Is.EqualTo(slot2AfterValue));
    }

    [Test]
    public void TestGetSlotAfterCommit()
    {
        using TestContext ctx = new TestContext();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        UInt256 slotIndex = 1;
        byte[] slotValue = { 0xCA, 0xFE };

        Account account = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(testAddress).Returns(account);

        // Write slot value
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(testAddress, 1);
            storageBatch.Set(slotIndex, slotValue);
            storageBatch.Dispose();
        }

        // Commit
        scope.Commit(1);

        // Verify slot exists in committed snapshot
        Assert.That(ctx.LastCommittedSnapshot, Is.Not.Null);
        bool found = ctx.LastCommittedSnapshot!.TryGetStorage(testAddress, slotIndex, out SlotValue? committedSlot);
        Assert.That(found, Is.True);
        Assert.That(committedSlot!.Value.ToEvmBytes(), Is.EqualTo(slotValue));
    }

    // ===== TRIE/STORAGE ROOT TESTS =====

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

    // ===== ACCOUNT SNAPSHOT COMMIT TESTS =====

    [Test]
    public void TestAccountCommittedInSnapshot()
    {
        using TestContext ctx = new TestContext();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        Account testAccount = new Account(100, 5000);

        ctx.PersistenceReader.GetAccount(testAddress).Returns(new Account(0, 0));

        // Set a single account
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            writeBatch.Set(testAddress, testAccount);
        }

        // Commit
        scope.Commit(1);

        // Verify account is in the committed snapshot
        Assert.That(ctx.LastCommittedSnapshot, Is.Not.Null);
        Assert.That(ctx.LastCommittedSnapshot!.TryGetAccount(testAddress, out Account? committedAccount), Is.True);
        Assert.That(committedAccount, Is.EqualTo(testAccount));
    }

    [Test]
    public void TestMultipleAccountsCommittedInSnapshot()
    {
        using TestContext ctx = new TestContext();
        FlatWorldStateScope scope = ctx.Scope;

        Address address1 = TestItem.AddressA;
        Address address2 = TestItem.AddressB;
        Address address3 = TestItem.AddressC;
        Account account1 = new Account(100, 1000);
        Account account2 = new Account(200, 2000);
        Account account3 = new Account(300, 3000);

        ctx.PersistenceReader.GetAccount(address1).Returns(new Account(0, 0));
        ctx.PersistenceReader.GetAccount(address2).Returns(new Account(0, 0));
        ctx.PersistenceReader.GetAccount(address3).Returns(new Account(0, 0));

        // Set multiple accounts in one commit
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(3))
        {
            writeBatch.Set(address1, account1);
            writeBatch.Set(address2, account2);
            writeBatch.Set(address3, account3);
        }

        scope.Commit(1);

        // Verify all accounts are in snapshot
        Assert.That(ctx.LastCommittedSnapshot, Is.Not.Null);
        Assert.That(ctx.LastCommittedSnapshot!.TryGetAccount(address1, out Account? acc1), Is.True);
        Assert.That(acc1, Is.EqualTo(account1));
        Assert.That(ctx.LastCommittedSnapshot!.TryGetAccount(address2, out Account? acc2), Is.True);
        Assert.That(acc2, Is.EqualTo(account2));
        Assert.That(ctx.LastCommittedSnapshot!.TryGetAccount(address3, out Account? acc3), Is.True);
        Assert.That(acc3, Is.EqualTo(account3));
    }

    [Test]
    public void TestAccountUpdatesOverwritePreviousValues()
    {
        using TestContext ctx = new TestContext();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        Account account1 = new Account(100, 1000);
        Account account2 = new Account(200, 2000);

        ctx.PersistenceReader.GetAccount(testAddress).Returns(new Account(0, 0));

        // First commit - set account
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            writeBatch.Set(testAddress, account1);
        }
        scope.Commit(1);

        Snapshot snapshot1 = ctx.LastCommittedSnapshot!;

        // Second commit - update account
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            writeBatch.Set(testAddress, account2);
        }
        scope.Commit(2);

        Snapshot snapshot2 = ctx.LastCommittedSnapshot!;

        // Verify first snapshot has account1
        Assert.That(snapshot1.TryGetAccount(testAddress, out Account? acc1), Is.True);
        Assert.That(acc1, Is.EqualTo(account1));

        // Verify second snapshot has account2 (not account1)
        Assert.That(snapshot2.TryGetAccount(testAddress, out Account? acc2), Is.True);
        Assert.That(acc2, Is.EqualTo(account2));
    }

    [Test]
    public void TestMultipleCommitsAccumulateAccounts()
    {
        using TestContext ctx = new TestContext();
        FlatWorldStateScope scope = ctx.Scope;

        Address address1 = TestItem.AddressA;
        Address address2 = TestItem.AddressB;
        Account account1 = new Account(100, 1000);
        Account account2 = new Account(200, 2000);

        ctx.PersistenceReader.GetAccount(address1).Returns(new Account(0, 0));
        ctx.PersistenceReader.GetAccount(address2).Returns(new Account(0, 0));

        // First commit
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            writeBatch.Set(address1, account1);
        }
        scope.Commit(1);

        // Second commit
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            writeBatch.Set(address2, account2);
        }
        scope.Commit(2);

        // Only the second commit's snapshot should have account2
        Assert.That(ctx.LastCommittedSnapshot!.TryGetAccount(address2, out Account? acc2), Is.True);
        Assert.That(acc2, Is.EqualTo(account2));

        // But reading from scope should see both (from current snapshot + previous snapshots)
        Assert.That(scope.Get(address1), Is.EqualTo(account1));
        Assert.That(scope.Get(address2), Is.EqualTo(account2));
    }

    // ===== COMPREHENSIVE SELFDESTRUCT BLOCKING TESTS =====

    [Test]
    public void TestSelfDestructBlocksAllEarlierSnapshots()
    {
        using TestContext ctx = new TestContext();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        UInt256 slotIndex = 1;
        byte[] slotValue1 = { 0x01 };
        byte[] slotValue2 = { 0x02 };
        byte[] slotValue3 = { 0x03 };

        Account account = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(testAddress).Returns(account);

        // Create multiple layers of snapshots with slot values
        // Snapshot 0: First slot value
        ctx.AddSnapshot(content => content.Storages[(testAddress, slotIndex)] = SlotValue.FromSpanWithoutLeadingZero(slotValue1));

        // Snapshot 1: Updated slot value
        ctx.AddSnapshot(content => content.Storages[(testAddress, slotIndex)] = SlotValue.FromSpanWithoutLeadingZero(slotValue2));

        // Snapshot 2: Another update
        ctx.AddSnapshot(content => content.Storages[(testAddress, slotIndex)] = SlotValue.FromSpanWithoutLeadingZero(slotValue3));

        // Snapshot 3: Empty snapshot
        ctx.AddSnapshot(content => { });

        // Snapshot 4: SELFDESTRUCT - this should block ALL earlier snapshots
        ctx.AddSnapshot(content => content.SelfDestructedStorageAddresses[testAddress] = false);

        // Snapshot 5: Empty snapshot after selfdestruct
        ctx.AddSnapshot(content => { });

        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(testAddress);
        byte[] result = storageTree.Get(slotIndex);

        // Should return zero bytes - ALL earlier snapshot values are blocked by selfdestruct
        Assert.That(result, Is.EqualTo(StorageTree.ZeroBytes));
    }

    [Test]
    public void TestSelfDestructBlocksPersistedSlotValue()
    {
        using TestContext ctx = new TestContext();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        UInt256 slotIndex = 1;
        byte[] persistedSlotValue = { 0xDE, 0xAD, 0xBE, 0xEF };

        Account account = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(testAddress).Returns(account);

        // Setup persistence to return a slot value
        SlotValue outValue = SlotValue.FromSpanWithoutLeadingZero(persistedSlotValue)!.Value;
        ctx.PersistenceReader.TryGetSlot(testAddress, slotIndex, ref Arg.Any<SlotValue>())
            .Returns(x => {
                x[2] = outValue;
                return true;
            });

        // Add several empty snapshots
        ctx.AddSnapshot(content => { });
        ctx.AddSnapshot(content => { });
        ctx.AddSnapshot(content => { });

        // Add selfdestruct in the last snapshot - should block persistence
        ctx.AddSnapshot(content => content.SelfDestructedStorageAddresses[testAddress] = false);

        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(testAddress);
        byte[] result = storageTree.Get(slotIndex);

        // Should return zero bytes - persisted value is blocked by selfdestruct
        Assert.That(result, Is.EqualTo(StorageTree.ZeroBytes));
    }

    [Test]
    public void TestSelfDestructBlocksEarlySnapshotAndPersistence()
    {
        using TestContext ctx = new TestContext();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        UInt256 slotIndex = 1;
        byte[] persistedSlotValue = { 0xAA, 0xBB, 0xCC, 0xDD };
        byte[] snapshot0Value = { 0x11, 0x22 };
        byte[] snapshot1Value = { 0x33, 0x44 };
        byte[] snapshot2Value = { 0x55, 0x66 };

        Account account = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(testAddress).Returns(account);

        // Setup persistence to return a slot value
        SlotValue outValue = SlotValue.FromSpanWithoutLeadingZero(persistedSlotValue)!.Value;
        ctx.PersistenceReader.TryGetSlot(testAddress, slotIndex, ref Arg.Any<SlotValue>())
            .Returns(x => {
                x[2] = outValue;
                return true;
            });

        // Snapshot 0: Slot value
        ctx.AddSnapshot(content => content.Storages[(testAddress, slotIndex)] = SlotValue.FromSpanWithoutLeadingZero(snapshot0Value));

        // Snapshot 1: Updated slot value
        ctx.AddSnapshot(content => content.Storages[(testAddress, slotIndex)] = SlotValue.FromSpanWithoutLeadingZero(snapshot1Value));

        // Snapshot 2: Another update
        ctx.AddSnapshot(content => content.Storages[(testAddress, slotIndex)] = SlotValue.FromSpanWithoutLeadingZero(snapshot2Value));

        // Snapshot 3: Empty
        ctx.AddSnapshot(content => { });

        // Snapshot 4: SELFDESTRUCT - blocks both snapshots AND persistence
        ctx.AddSnapshot(content => content.SelfDestructedStorageAddresses[testAddress] = false);

        // Snapshot 5: Empty after selfdestruct
        ctx.AddSnapshot(content => { });

        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(testAddress);
        byte[] result = storageTree.Get(slotIndex);

        // Should return zero bytes - neither snapshot values nor persistence value should appear
        Assert.That(result, Is.EqualTo(StorageTree.ZeroBytes));
    }


}
