// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Buffers;
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
using Nethermind.Trie.Sparse;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class FlatWorldStateScopeProviderTests
{

    private class TestContext : IDisposable
    {
        private readonly ContainerBuilder _containerBuilder;
        private readonly CancellationTokenSource _cancellationTokenSource = new();

        private IContainer? _container;
        private IContainer Container => _container ??= _containerBuilder.Build();

        public ResourcePool ResourcePool => field ??= Container.Resolve<ResourcePool>();
        public SnapshotBundle SnapshotBundle => Container.Resolve<SnapshotBundle>();
        public SnapshotPooledList ReadOnlySnapshots = new(0);
        public IPersistence.IPersistenceReader PersistenceReader => field ??= Container.Resolve<IPersistence.IPersistenceReader>();
        public Snapshot? LastCommittedSnapshot { get; set; }
        public TransientResource? LastCreatedCachedResource { get; set; }

        public TestContext(FlatDbConfig? config = null, ITrieWarmer? trieWarmer = null)
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
                .WithParameter(TypedParameter.From(new StateId(0, Keccak.EmptyTreeHash)))
                ;

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

    [TestCase(false)]
    [TestCase(true)]
    public void GetAccount_ReportsWhetherAccountIsInCurrentSnapshot(bool isNull)
    {
        using TestContext ctx = new();
        Address address = TestItem.AddressA;
        Account? account = isNull ? null : TestItem.GenerateRandomAccount();

        ctx.SnapshotBundle.SetAccount(address, account);

        Account? result = ctx.SnapshotBundle.GetAccount(address, out bool isInCurrentSnapshot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(account));
            Assert.That(isInCurrentSnapshot, Is.True);
        }
    }

    [Test]
    public void GetAccount_ReportsAccountFromPersistenceIsNotInCurrentSnapshot()
    {
        using TestContext ctx = new();
        Address address = TestItem.AddressA;
        Account account = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(address).Returns(account);

        Account? result = ctx.SnapshotBundle.GetAccount(address, out bool isInCurrentSnapshot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(account));
            Assert.That(isInCurrentSnapshot, Is.False);
        }
    }

    [Test]
    public void Get_PromotesAccountFromPersistenceIntoCurrentSnapshot()
    {
        using TestContext ctx = new();
        Address address = TestItem.AddressA;
        Account account = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(address).Returns(account);

        Assert.That(ctx.Scope.Get(address), Is.EqualTo(account));

        Account? promoted = ctx.SnapshotBundle.GetAccount(address, out bool isInCurrentSnapshot);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(promoted, Is.EqualTo(account));
            Assert.That(isInCurrentSnapshot, Is.True);
        }
    }

    [Test]
    public void HintGet_DoesNotOverwriteDirtyAccount()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;
        Address address = TestItem.AddressA;
        Account dirtyAccount = TestItem.GenerateIndexedAccount(1);
        Account staleAccount = TestItem.GenerateIndexedAccount(0);

        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            writeBatch.Set(address, dirtyAccount);
        }

        scope.HintGet(address, staleAccount);

        Assert.That(scope.Get(address), Is.EqualTo(dirtyAccount));
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
    public void StorageRootAfterParallelCommitMatchesRawTrie()
    {
        const int slotsPerCommit = 1024;
        const int commitCount = 2;
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;
        Address address = TestItem.AddressA;

        ctx.PersistenceReader.GetAccount(address).Returns(TestItem.GenerateRandomAccount());

        for (int commit = 0; commit < commitCount; commit++)
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(commit + 1))
            {
                using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(address, slotsPerCommit);
                int firstSlot = commit * slotsPerCommit + 1;
                int lastSlot = firstSlot + slotsPerCommit;
                for (int i = firstSlot; i < lastSlot; i++) storageBatch.Set((UInt256)i, [(byte)i, (byte)(i >> 8)]);
            }

            scope.Commit((ulong)(commit + 1));
        }

        TestMemDb testDb = new();
        RawScopedTrieStore trieStore = new(testDb);
        StorageTree expectedTree = new(trieStore, LimboLogs.Instance);
        for (int i = 1; i <= slotsPerCommit * commitCount; i++) expectedTree.Set((UInt256)i, [(byte)i, (byte)(i >> 8)]);
        expectedTree.UpdateRootHash();

        Account? account = scope.Get(address);
        Assert.That(account, Is.Not.Null);
        Assert.That(account!.StorageRoot, Is.EqualTo(expectedTree.RootHash));
    }

    [Test]
    public void VerifyWithTrie_DualRun_AgreesAcrossStorageStateAndSelfDestruct()
    {
        // Every stage self-checks: storage roots per job, state roots at UpdateRootHash/Commit,
        // and account/slot reads against the diagnostic Patricia trees.
        using TestContext ctx = new(new FlatDbConfig { VerifyWithTrie = true });
        FlatWorldStateScope scope = ctx.Scope;

        Address contract = TestItem.AddressA;
        Address plain = TestItem.AddressB;
        Account contractAccount = TestItem.GenerateRandomAccount();
        Account plainAccount = TestItem.GenerateRandomAccount();

        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(2))
        {
            using (IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(contract, 2))
            {
                storageBatch.Set(1, [0x01]);
                storageBatch.Set(2, [0x02, 0x03]);
            }

            writeBatch.Set(contract, contractAccount);
            writeBatch.Set(plain, plainAccount);
        }

        scope.UpdateRootHash();
        scope.Commit(1);

        // Cache the storage tree ahead of the deletion: the verify-mode Get check inside
        // CreateStorageTreeImpl races the flat dirty null against the not-yet-updated
        // diagnostic Patricia tree (pre-existing VerifyWithTrie behavior).
        scope.CreateStorageTree(plain);

        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(2))
        {
            using (IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(contract, 1))
            {
                storageBatch.Clear();
                storageBatch.Set(7, [0x07]);
            }

            writeBatch.Set(contract, contractAccount);
            writeBatch.Set(plain, null);
        }

        scope.UpdateRootHash();
        scope.Commit(2);

        TestMemDb testDb = new();
        StorageTree expectedTree = new(new RawScopedTrieStore(testDb), LimboLogs.Instance);
        expectedTree.Set(7, [0x07]);
        expectedTree.UpdateRootHash();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.Get(contract)!.StorageRoot, Is.EqualTo(expectedTree.RootHash));
            Assert.That(scope.Get(plain), Is.Null);
        }
    }

    [Test]
    public void DeletedFinalAccount_SkipsItsStorageJob_ButKeepsDeletionAndClear()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        Address address = TestItem.AddressA;
        Account account = TestItem.GenerateRandomAccount();

        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            using (IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(address, 1))
            {
                storageBatch.Set(1, [0x01]);
            }

            writeBatch.Set(address, account);
        }

        scope.Commit(1);

        // Modify the existing storage, then destroy the account in the same block: the sealed
        // job must be discarded while the account deletion and the storage clear survive.
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            using (IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(address, 1))
            {
                storageBatch.Set(3, [0x03]);
            }

            writeBatch.Set(address, null);
        }

        scope.Commit(2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.Get(address), Is.Null);
            Assert.That(scope.CreateStorageTree(address).Get(1), Is.EqualTo(StorageTree.ZeroBytes), "committed slot cleared");
            Assert.That(scope.CreateStorageTree(address).Get(3), Is.EqualTo(StorageTree.ZeroBytes), "same-block slot cleared");
        }
    }

    [Test]
    public void FailedStorageCalculation_PoisonsTheScope_AndCommitRejects()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        Address address = TestItem.AddressA;
        // An unresolvable storage root: the reveal finds no committed node anywhere.
        Account account = TestItem.GenerateRandomAccount().WithChangedStorageRoot(Keccak.Compute("missing"));
        ctx.PersistenceReader.GetAccount(address).Returns(account);
        ctx.PersistenceReader.TryLoadStorageRlp(Arg.Any<Hash256>(), Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns((byte[]?)null);

        Assert.Throws<MissingTrieNodeException>(() =>
        {
            using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1);
            using (IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(address, 1))
            {
                storageBatch.Set(1, [0x01]);
            }

            writeBatch.Set(address, account);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.RootHash, Is.EqualTo(Keccak.EmptyTreeHash), "root stays at the anchor");
            Assert.Throws<InvalidOperationException>(() => scope.UpdateRootHash(), "recalculation rejected");
            Assert.Throws<InvalidOperationException>(() => scope.Commit(1), "commit rejected");
        }
    }

    [Test]
    public void FiveStorageTries_RunTheParallelJobPhase_AndPropagateEveryRoot()
    {
        // Five jobs exceed the serial threshold, so this exercises the ParallelUnbalancedWork path.
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        Address[] addresses = [TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD, TestItem.AddressE];
        foreach (Address address in addresses)
        {
            ctx.PersistenceReader.GetAccount(address).Returns(TestItem.GenerateRandomAccount());
        }

        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(addresses.Length))
        {
            for (int i = 0; i < addresses.Length; i++)
            {
                using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(addresses[i], 3);
                for (int slot = 1; slot <= 3; slot++)
                {
                    storageBatch.Set((UInt256)(i * 100 + slot), [(byte)(i + 1), (byte)slot]);
                }
            }
        }

        scope.Commit(1);

        for (int i = 0; i < addresses.Length; i++)
        {
            TestMemDb testDb = new();
            StorageTree expectedTree = new(new RawScopedTrieStore(testDb), LimboLogs.Instance);
            for (int slot = 1; slot <= 3; slot++)
            {
                expectedTree.Set((UInt256)(i * 100 + slot), [(byte)(i + 1), (byte)slot]);
            }

            expectedTree.UpdateRootHash();
            Assert.That(scope.Get(addresses[i])!.StorageRoot, Is.EqualTo(expectedTree.RootHash), $"address {i}");
        }
    }

    [Test]
    public void StateRoot_AnchorBeforeCalculation_InvalidatedByLaterBatches_AndReusableAfterCommit()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        Account accountA = TestItem.GenerateRandomAccount();
        Account accountB = TestItem.GenerateRandomAccount();
        Account accountC = TestItem.GenerateRandomAccount();

        Assert.That(scope.RootHash, Is.EqualTo(Keccak.EmptyTreeHash), "anchor before any calculation");

        TestMemDb testDb = new();
        StateTree expectedTree = new(new RawScopedTrieStore(testDb), LimboLogs.Instance);

        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            writeBatch.Set(TestItem.AddressA, accountA);
        }

        scope.UpdateRootHash();
        Hash256 root1 = scope.RootHash;
        expectedTree.Set(TestItem.AddressA, accountA);
        expectedTree.UpdateRootHash();
        Assert.That(root1, Is.EqualTo(expectedTree.RootHash), "intermediate root");

        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            writeBatch.Set(TestItem.AddressB, accountB);
        }

        scope.UpdateRootHash();
        expectedTree.Set(TestItem.AddressB, accountB);
        expectedTree.UpdateRootHash();
        Assert.That(scope.RootHash, Is.EqualTo(expectedTree.RootHash), "later batch invalidates the earlier root");

        scope.Commit(1);

        // The next block anchors at the committed root and reveals the just-published nodes.
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            writeBatch.Set(TestItem.AddressC, accountC);
        }

        scope.UpdateRootHash();
        expectedTree.Set(TestItem.AddressC, accountC);
        expectedTree.UpdateRootHash();
        Assert.That(scope.RootHash, Is.EqualTo(expectedTree.RootHash), "root across the published block boundary");

        scope.Commit(2);
    }

    [Test]
    public void Publication_AdoptsTheStagedRlpArrays_WithoutCopying()
    {
        // The staged array is the final owned RLP; a copying TrieNode overload (e.g. the
        // ReadOnlySpan one, which a bare byte[] binds to) would duplicate every published node.
        byte[] rlpA = new byte[40];
        byte[] rlpB = new byte[64];
        for (int i = 0; i < rlpB.Length; i++) rlpB[i] = (byte)i;

        using ArrayPoolList<SparseTrieStagedNode> staged = new(2)
        {
            new SparseTrieStagedNode(TreePath.FromHexString("1"), ValueKeccak.Compute(rlpA), rlpA),
            new SparseTrieStagedNode(TreePath.FromHexString("2f"), ValueKeccak.Compute(rlpB), rlpB),
        };

        List<(TreePath, TrieNode)> buffer = FlatSparseTrieSession.BuildPublicationBuffer(staged);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReferenceEquals(buffer[0].Item2.FullRlp.UnderlyingArray, rlpA), "first staged array adopted");
            Assert.That(ReferenceEquals(buffer[1].Item2.FullRlp.UnderlyingArray, rlpB), "second staged array adopted");
            Assert.That(buffer[0].Item2.IsSealed, "published node sealed");
            Assert.That(buffer[0].Item2.Keccak, Is.EqualTo(ValueKeccak.Compute(rlpA).ToCommitment()));
        }
    }

    [Test]
    public void EmptyBlockCommit_KeepsTheParentRoot()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            writeBatch.Set(TestItem.AddressA, TestItem.GenerateRandomAccount());
        }

        scope.UpdateRootHash();
        Hash256 root = scope.RootHash;
        scope.Commit(1);

        scope.Commit(2);
        Assert.That(scope.RootHash, Is.EqualTo(root));
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
            new StateId(0, TestItem.KeccakA),
            bundle,
            new TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb(new TestMemDb()),
            Substitute.For<IFlatCommitTarget>(),
            config,
            warmer,
            LimboLogs.Instance);

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

[TestFixture]
public class FlatTrieNodeReaderTests
{
    private class TestContext : IDisposable
    {
        private readonly ContainerBuilder _containerBuilder;
        private IContainer? _container;
        private IContainer Container => _container ??= _containerBuilder.Build();
        private Snapshot? _collectedSnapshot;
        private TransientResource? _collectedResource;

        public SnapshotPooledList ReadOnlySnapshots = new(0);

        public ResourcePool ResourcePool => field ??= Container.Resolve<ResourcePool>();
        public SnapshotBundle SnapshotBundle => Container.Resolve<SnapshotBundle>();
        public ITrieNodeCache TrieNodeCache => Container.Resolve<ITrieNodeCache>();
        public IPersistence.IPersistenceReader PersistenceReader => field ??= Container.Resolve<IPersistence.IPersistenceReader>();

        public TestContext()
        {
            _containerBuilder = new ContainerBuilder()
                .AddModule(new FlatWorldStateModule(new FlatDbConfig()))
                .AddSingleton<IPersistence.IPersistenceReader>(_ =>
                {
                    // NSubstitute auto-returns empty arrays; a missing node must be null.
                    IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
                    reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns((byte[]?)null);
                    reader.TryLoadStorageRlp(Arg.Any<Hash256>(), Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns((byte[]?)null);
                    return reader;
                })
                .AddSingleton<IFlatDbManager>(_ => Substitute.For<IFlatDbManager>())
                .Bind<IFlatCommitTarget, IFlatDbManager>()
                .AddSingleton<IProcessExitSource>(_ => Substitute.For<IProcessExitSource>())
                .AddSingleton<ILogManager>(LimboLogs.Instance)
                .AddSingleton<IFlatDbConfig>(new FlatDbConfig())
                .AddSingleton<IInitConfig>(_ => Substitute.For<IInitConfig>());

            _containerBuilder.RegisterType<ReadOnlySnapshotBundle>()
                .WithParameter(TypedParameter.From(false)) // recordDetailedMetrics
                .WithParameter(TypedParameter.From(ReadOnlySnapshots))
                .WithParameter(TypedParameter.From(PersistedSnapshotStack.Empty()))
                .ExternallyOwned();

            _containerBuilder.RegisterType<SnapshotBundle>()
                .SingleInstance()
                .WithParameter(TypedParameter.From(ResourcePool.Usage.MainBlockProcessing))
                .ExternallyOwned();
        }

        /// <summary>Adds a committed snapshot to the read-only (older) tier.</summary>
        public void AddReadOnlySnapshot(Action<SnapshotContent> populator)
        {
            SnapshotContent content = ResourcePool.GetSnapshotContent(ResourcePool.Usage.MainBlockProcessing);
            populator(content);
            ReadOnlySnapshots.Add(new Snapshot(StateId.PreGenesis, StateId.PreGenesis, content, ResourcePool, ResourcePool.Usage.MainBlockProcessing));
        }

        /// <summary>Commits the bundle's current write buffer into its in-memory snapshot tier.</summary>
        public void CollectCurrentIntoSnapshot() =>
            (_collectedSnapshot, _collectedResource) = SnapshotBundle.CollectAndApplySnapshot(
                new StateId(0, Keccak.EmptyTreeHash), new StateId(1, Keccak.OfAnEmptyString), returnSnapshot: true);

        public void Dispose()
        {
            SnapshotBundle.Dispose();
            _collectedSnapshot?.Dispose();
            if (_collectedResource is not null)
            {
                ResourcePool.ReturnCachedResource(ResourcePool.Usage.MainBlockProcessing, _collectedResource);
            }

            _container?.Dispose();
        }
    }

    private static byte[] TestRlp(int seed)
    {
        byte[] rlp = new byte[40 + seed % 8];
        for (int i = 0; i < rlp.Length; i++)
        {
            rlp[i] = (byte)(seed * 17 + i);
        }

        return rlp;
    }

    private static TrieNode SealedNode(byte[] rlp) =>
        new(NodeType.Unknown, ValueKeccak.Compute(rlp).ToCommitment(), rlp);

    private static CappedArray<byte> ResolveOne(FlatTrieNodeReader reader, in TreePath path, in ValueHash256 hash)
    {
        SparseNodeRequest[] requests = [new(in path, in hash)];
        CappedArray<byte>[] results = new CappedArray<byte>[1];
        reader.Resolve(requests, results);
        return results[0];
    }

    private static TreePath Path(string nibbles) => TreePath.FromHexString(nibbles);

    [Test]
    public void Reads_committed_node_from_current_bundle_snapshot()
    {
        using TestContext ctx = new();
        byte[] rlp = TestRlp(1);
        TrieNode node = SealedNode(rlp);
        TreePath path = Path("12a");

        ctx.SnapshotBundle.SetStateNode(in path, node);
        ctx.CollectCurrentIntoSnapshot();

        FlatTrieNodeReader reader = new(ctx.SnapshotBundle, address: null);
        Assert.That(ResolveOne(reader, in path, node.Keccak!.ValueHash256).AsSpan().SequenceEqual(rlp));
    }

    [Test]
    public void Current_scope_dirty_node_cannot_shadow_the_committed_node_at_its_path()
    {
        using TestContext ctx = new();
        byte[] committedRlp = TestRlp(2);
        byte[] dirtyRlp = TestRlp(20);
        TrieNode committed = SealedNode(committedRlp);
        TrieNode dirty = SealedNode(dirtyRlp);
        TreePath path = Path("12a");

        ctx.AddReadOnlySnapshot(content => content.StateNodes[path] = committed);
        ctx.SnapshotBundle.SetStateNode(in path, dirty);

        // The path-keyed dirty tier (which Patricia reads consult first, without a hash check)
        // is excluded; the committed node behind it stays reachable. Every tier the committed
        // reader does consult is (path, hash)-keyed, so a dirty entry can only ever satisfy a
        // request for its own exact bytes, which is harmless by construction.
        FlatTrieNodeReader reader = new(ctx.SnapshotBundle, address: null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ResolveOne(reader, in path, committed.Keccak!.ValueHash256).AsSpan().SequenceEqual(committedRlp), "committed node reachable behind the dirty entry");
            Assert.That(ResolveOne(reader, in path, dirty.Keccak!.ValueHash256).AsSpan().SequenceEqual(dirtyRlp), "hash-keyed hit returns exactly the requested bytes");
        }
    }

    [Test]
    public void Skips_newer_version_at_same_path_and_finds_older()
    {
        using TestContext ctx = new();
        byte[] olderRlp = TestRlp(3);
        byte[] newerRlp = TestRlp(4);
        TrieNode older = SealedNode(olderRlp);
        TrieNode newer = SealedNode(newerRlp);
        TreePath path = Path("3f");

        ctx.AddReadOnlySnapshot(content => content.StateNodes[path] = older);
        ctx.SnapshotBundle.SetStateNode(in path, newer);
        ctx.CollectCurrentIntoSnapshot();

        FlatTrieNodeReader reader = new(ctx.SnapshotBundle, address: null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ResolveOne(reader, in path, newer.Keccak!.ValueHash256).AsSpan().SequenceEqual(newerRlp), "newer version");
            Assert.That(ResolveOne(reader, in path, older.Keccak!.ValueHash256).AsSpan().SequenceEqual(olderRlp), "older version behind a newer path match");
        }
    }

    [Test]
    public void Reads_from_trie_node_cache()
    {
        using TestContext ctx = new();
        byte[] rlp = TestRlp(5);
        TrieNode node = SealedNode(rlp);
        TreePath path = Path("ab3");

        TransientResource transient = ctx.ResourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transient.UpdateStateNode(in path, node);
        ctx.TrieNodeCache.Add(transient);
        ctx.ResourcePool.ReturnCachedResource(ResourcePool.Usage.MainBlockProcessing, transient);

        FlatTrieNodeReader reader = new(ctx.SnapshotBundle, address: null);
        Assert.That(ResolveOne(reader, in path, node.Keccak!.ValueHash256).AsSpan().SequenceEqual(rlp));
    }

    [Test]
    public void Reads_from_persistence_and_validates_hash()
    {
        using TestContext ctx = new();
        byte[] rlp = TestRlp(6);
        TreePath path = Path("77");
        ctx.PersistenceReader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(rlp);

        FlatTrieNodeReader reader = new(ctx.SnapshotBundle, address: null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ResolveOne(reader, in path, ValueKeccak.Compute(rlp)).AsSpan().SequenceEqual(rlp), "matching hash");
        }

        ValueHash256 wrongHash = ValueKeccak.Compute([1, 2, 3]);
        Assert.Throws<NodeHashMismatchException>(() => ResolveOne(reader, in path, in wrongHash));
    }

    [Test]
    public void Missing_node_resolves_to_null()
    {
        using TestContext ctx = new();
        ctx.PersistenceReader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns((byte[]?)null);

        FlatTrieNodeReader reader = new(ctx.SnapshotBundle, address: null);
        TreePath path = Path("9");
        ValueHash256 hash = ValueKeccak.Compute([9]);
        Assert.That(ResolveOne(reader, in path, in hash).IsNull);
    }

    [Test]
    public void Storage_reads_are_address_bound()
    {
        using TestContext ctx = new();
        Hash256 addressA = ValueKeccak.Compute([0xA]).ToCommitment();
        Hash256 addressB = ValueKeccak.Compute([0xB]).ToCommitment();
        byte[] rlpA = TestRlp(7);
        byte[] rlpB = TestRlp(8);
        TrieNode nodeA = SealedNode(rlpA);
        TrieNode nodeB = SealedNode(rlpB);
        TreePath path = Path("c2");

        ctx.AddReadOnlySnapshot(content =>
        {
            content.StorageNodes.GetOrAddAddress(addressA).Set(in path, nodeA);
            content.StorageNodes.GetOrAddAddress(addressB).Set(in path, nodeB);
        });

        FlatTrieNodeReader readerA = new(ctx.SnapshotBundle, addressA);
        FlatTrieNodeReader readerB = new(ctx.SnapshotBundle, addressB);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ResolveOne(readerA, in path, nodeA.Keccak!.ValueHash256).AsSpan().SequenceEqual(rlpA), "address A");
            Assert.That(ResolveOne(readerB, in path, nodeB.Keccak!.ValueHash256).AsSpan().SequenceEqual(rlpB), "address B");
            Assert.That(ResolveOne(readerA, in path, nodeB.Keccak!.ValueHash256).IsNull, "cross-address isolation");
        }
    }

    [Test]
    public void Storage_persistence_reads_validate_hash()
    {
        using TestContext ctx = new();
        Hash256 address = ValueKeccak.Compute([0xC]).ToCommitment();
        byte[] rlp = TestRlp(9);
        TreePath path = Path("d");
        ctx.PersistenceReader.TryLoadStorageRlp(address, Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(rlp);

        FlatTrieNodeReader reader = new(ctx.SnapshotBundle, address);
        Assert.That(ResolveOne(reader, in path, ValueKeccak.Compute(rlp)).AsSpan().SequenceEqual(rlp));

        ValueHash256 wrongHash = ValueKeccak.Compute([4, 5, 6]);
        Assert.Throws<NodeHashMismatchException>(() => ResolveOne(reader, in path, in wrongHash));
    }

    [Test]
    public void Batch_preserves_request_association()
    {
        using TestContext ctx = new();
        byte[] rlpX = TestRlp(10);
        byte[] rlpY = TestRlp(11);
        TrieNode nodeX = SealedNode(rlpX);
        TrieNode nodeY = SealedNode(rlpY);
        TreePath pathX = Path("1");
        TreePath pathY = Path("2");

        ctx.AddReadOnlySnapshot(content =>
        {
            content.StateNodes[pathX] = nodeX;
            content.StateNodes[pathY] = nodeY;
        });

        FlatTrieNodeReader reader = new(ctx.SnapshotBundle, address: null);
        SparseNodeRequest[] requests =
        [
            new(in pathX, nodeX.Keccak!.ValueHash256),
            new(in pathY, nodeY.Keccak!.ValueHash256),
            new(in pathX, nodeX.Keccak!.ValueHash256),
        ];
        CappedArray<byte>[] results = new CappedArray<byte>[3];
        reader.Resolve(requests, results);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].AsSpan().SequenceEqual(rlpX), "slot 0");
            Assert.That(results[1].AsSpan().SequenceEqual(rlpY), "slot 1");
            Assert.That(results[2].AsSpan().SequenceEqual(rlpX), "duplicate slot 2");
        }
    }
}
