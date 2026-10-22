// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class FlatWorldStateScopeProviderTests
{
    [Test]
    public void Buffer_contract_alias_uses_the_decorated_singleton_for_scope_writes()
    {
        using TestContext context = new(decorateBuffer: true);
        INodeRecordBuffer retained = context.NodeRecordBuffer;
        INodeRecordBuffer common = context.Container.Resolve<INodeRecordBuffer>();
        ForwardingNodeRecordBuffer forwarding = (ForwardingNodeRecordBuffer)retained;
        FlatWorldStateScope scope = context.Scope;
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
            batch.Set(TestItem.AddressA, new Account(nonce: 1, balance: 2));
        scope.Commit(1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(common, Is.SameAs(retained));
            Assert.That(context.Container.Resolve<INodeRecordBuffer>(), Is.SameAs(retained));
            Assert.That(forwarding.CompletedRecords, Is.GreaterThan(0));
            Assert.That(context.LastCommittedSnapshot!.BufferPin!.Buffer, Is.SameAs(retained));
        }
    }

    [Test]
    public void TryBeginScope_ReturnsFalseWhenStateIsRemovedAfterAdvisoryCheck()
    {
        bool stateAvailable = true;
        BlockHeader parent = Build.A.BlockHeader.WithNumber(1).TestObject;
        BlockHeader target = Build.A.BlockHeader.WithParent(parent).WithTimestamp(1234).TestObject;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(parent);

        using TestContext context = new(blockTree);
        IWorldStateManager manager = context.WorldStateManager;
        context.FlatDbManager.HasStateForBlock(Arg.Any<StateId>()).Returns(_ => stateAvailable);
        context.FlatDbManager.GatherSnapshotBundle(Arg.Any<StateId>(), Arg.Any<ResourcePool.Usage>(), Arg.Any<bool>()).Returns(_ =>
        {
            if (!stateAvailable) throw CreateStateUnavailableException();
            return CreateSnapshotBundle(context.ResourcePool, context.NodeRecordBuffer);
        });

        IWorldStateScopeProvider provider = manager.GlobalWorldState;
        Assert.That(provider.HasStateForTargetBlock(target), Is.True);

        stateAvailable = false;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.TryBeginScopeAtTarget(target, new LocalMetrics(), out IWorldStateScopeProvider.IScope? scope), Is.False);
            Assert.That(scope, Is.Null);
        }
    }

    [Test]
    public void ManagerCreatedProviders_OpenTargetAtSameParent()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        BlockHeader parent = Build.A.BlockHeader.WithNumber(1).WithStateRoot(TestItem.KeccakA).TestObject;
        BlockHeader target = Build.A.BlockHeader.WithParent(parent).WithTimestamp(1).TestObject;
        BlockHeader sameParentDifferentTimestamp = Build.A.BlockHeader.WithParent(parent).WithTimestamp(2).TestObject;
        // Only the exact parent lookup answers, so a provider resolving anything else gets no parent at all.
        blockTree.FindHeader(parent.Hash!, BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing, parent.Number).Returns(parent);

        using TestContext context = new(blockTree);
        IWorldStateManager manager = context.WorldStateManager;
        IWorldStateScopeProvider[] providers =
        [
            manager.GlobalWorldState,
            manager.CreateResettableWorldState(),
        ];
        using IOverridableWorldScope overridable = manager.CreateOverridableWorldScope();
        providers = [.. providers, overridable.WorldState];

        foreach (IWorldStateScopeProvider provider in providers)
        {
            Assert.That(provider.HasStateForTargetBlock(target), Is.True);
            Assert.That(provider.TryBeginScopeAtTarget(target, new LocalMetrics(), out IWorldStateScopeProvider.IScope? scope), Is.True);
            scope!.Dispose();
        }

        Assert.That(manager.GlobalWorldState.HasStateForTargetBlock(sameParentDifferentTimestamp), Is.True);
        Assert.That(manager.GlobalWorldState.TryBeginScopeAtTarget(sameParentDifferentTimestamp, new LocalMetrics(), out IWorldStateScopeProvider.IScope? secondScope), Is.True);
        secondScope!.Dispose();

        using (Assert.EnterMultipleScope())
        {
            context.FlatDbManager.Received().GatherSnapshotBundle(new StateId(parent), Arg.Any<ResourcePool.Usage>(), Arg.Any<bool>());
            context.FlatDbManager.DidNotReceive().GatherSnapshotBundle(new StateId(target), Arg.Any<ResourcePool.Usage>(), Arg.Any<bool>());
        }
    }

    [TestCase(ScopeProviderKind.Global, false, TestName = "GatherSnapshotBundle_ForMainBlockProcessing_DoesNotFilterSlotReads")]
    [TestCase(ScopeProviderKind.Resettable, false, TestName = "GatherSnapshotBundle_ForResettableWorldState_DoesNotFilterSlotReads")]
    [TestCase(ScopeProviderKind.ReadOnlyQuery, true, TestName = "GatherSnapshotBundle_ForReadOnlyQueryWorldState_FiltersSlotReads")]
    public void GatherSnapshotBundle_FiltersSlotReadsOnlyForReadOnlyQueries(ScopeProviderKind kind, bool expectedFilter)
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(1).WithStateRoot(TestItem.KeccakA).TestObject;
        using TestContext context = new();
        IWorldStateManager manager = context.WorldStateManager;
        IWorldStateScopeProvider provider = kind switch
        {
            ScopeProviderKind.Global => manager.GlobalWorldState,
            ScopeProviderKind.Resettable => manager.CreateResettableWorldState(),
            _ => manager.CreateReadOnlyQueryWorldState(),
        };

        Assert.That(provider.TryBeginScope(parent, new LocalMetrics(), out IWorldStateScopeProvider.IScope? scope), Is.True,
            "precondition: the stubbed manager serves every base block");
        scope!.Dispose();

        context.FlatDbManager.Received(1).GatherSnapshotBundle(new StateId(parent), Arg.Any<ResourcePool.Usage>(), expectedFilter);
        context.FlatDbManager.DidNotReceive().GatherSnapshotBundle(Arg.Any<StateId>(), Arg.Any<ResourcePool.Usage>(), !expectedFilter);
    }

    [Test]
    public void TryBeginScope_RetainsReaderUntilActiveScopeIsDisposed()
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(1).TestObject;
        BlockHeader target = Build.A.BlockHeader.WithParent(parent).WithTimestamp(5678).TestObject;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(parent);
        IPersistence.IPersistenceReader persistenceReader = Substitute.For<IPersistence.IPersistenceReader>();
        bool readerDisposed = false;
        persistenceReader.When(reader => reader.Dispose()).Do(_ => readerDisposed = true);

        using TestContext context = new(blockTree);
        context.FlatDbManager.GatherSnapshotBundle(Arg.Any<StateId>(), Arg.Any<ResourcePool.Usage>(), Arg.Any<bool>())
            .Returns(_ => CreateSnapshotBundle(context.ResourcePool, context.NodeRecordBuffer, persistenceReader));

        Assert.That(context.WorldStateManager.GlobalWorldState.TryBeginScopeAtTarget(target, new LocalMetrics(), out IWorldStateScopeProvider.IScope? scope), Is.True);
        Assert.That(readerDisposed, Is.False);

        scope!.Dispose();
        Assert.That(readerDisposed, Is.True);
    }

    [Test]
    public void TargetAcquisitionFailureAfterLocalLeaseReleasesSnapshots()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        BlockHeader parent = Build.A.BlockHeader.WithNumber(1).TestObject;
        BlockHeader target = Build.A.BlockHeader.WithParent(parent).TestObject;
        blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(parent);
        using TestContext context = new(blockTree);
        Snapshot local = context.ResourcePool.CreateSnapshot(StateId.PreGenesis, new StateId(parent), ResourcePool.Usage.MainBlockProcessing);
        TransientResource resource = context.ResourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        IOverridableWorldScope overridable = context.WorldStateManager.CreateOverridableWorldScope();
        ((FlatOverridableWorldScope)overridable).AddSnapshot(local, resource);
        context.FlatDbManager.GatherReadOnlySnapshotBundle(Arg.Any<StateId>())
            .Returns(_ => throw CreateStateUnavailableException());

        Assert.That(overridable.WorldState.TryBeginScopeAtTarget(target, new LocalMetrics(), out _), Is.False);
        Assert.That(local.ToString(), Is.EqualTo("Leases: 1"));
        overridable.Dispose();
    }

    private static Exception CreateStateUnavailableException() => new StateUnavailableException("state removed during acquisition");

    private static SnapshotBundle CreateSnapshotBundle(ResourcePool resourcePool, INodeRecordBuffer nodeRecordBuffer, IPersistence.IPersistenceReader? persistenceReader = null)
    {
        persistenceReader ??= Substitute.For<IPersistence.IPersistenceReader>();
        return new SnapshotBundle(
            new ReadOnlySnapshotBundle(new SnapshotPooledList(0), persistenceReader, false, PersistedSnapshotStack.Empty(), bufferPin: null),
            nodeRecordBuffer,
            resourcePool,
            ResourcePool.Usage.ReadOnlyProcessingEnv);
    }

    private class TestContext : IDisposable
    {
        private readonly ContainerBuilder _containerBuilder;
        private readonly CancellationTokenSource _cancellationTokenSource = new();

        private IContainer? _container;
        public IContainer Container => _container ??= _containerBuilder.Build();

        public ResourcePool ResourcePool => field ??= Container.Resolve<ResourcePool>();
        public INodeRecordBuffer NodeRecordBuffer => field ??= Container.Resolve<INodeRecordBuffer>();
        public IFlatDbManager FlatDbManager => field ??= Container.Resolve<IFlatDbManager>();
        public IWorldStateManager WorldStateManager => field ??= Container.Resolve<IWorldStateManager>();
        public SnapshotBundle SnapshotBundle => Container.Resolve<SnapshotBundle>();
        public SnapshotPooledList ReadOnlySnapshots = new(0);
        public List<Snapshot> LocalSnapshots { get; } = [];
        public IPersistence.IPersistenceReader PersistenceReader => field ??= Container.Resolve<IPersistence.IPersistenceReader>();
        public Snapshot? LastCommittedSnapshot { get; set; }

        public TestContext(IBlockTree? blockTree = null, FlatDbConfig? config = null, ITrieWarmer? trieWarmer = null, bool historical = false, Hash256? stateRoot = null, bool decorateBuffer = false, IPersistence.IPersistenceReader? persistenceReader = null)
        {
            config ??= new FlatDbConfig();

            _containerBuilder = new ContainerBuilder()
                    .AddModule(new FlatWorldStateModule(config))
                    .AddSingleton<INodeRecordBuffer>(_ => FlatTestHelpers.NewTestBuffer())
                    .Bind<IWorldStateManager, FlatWorldStateManager>()
                    .AddSingleton<IPersistence>(Substitute.For<IPersistence>())
                    .AddSingleton<IFlatStateRootIndex>(Substitute.For<IFlatStateRootIndex>())
                    .AddSingleton<IStateHeaderProvider>(new ReorgDepthStateHeaderProvider(blockTree ?? Substitute.For<IBlockTree>()))
                    .AddKeyedSingleton<IDb>(DbNames.Code, new TestMemDb())
                    .AddSingleton<IBlockTree>(blockTree ?? Substitute.For<IBlockTree>())
                    .AddSingleton<ITrieWarmer, NoopTrieWarmer>()
                    .AddSingleton<IPersistence.IPersistenceReader>(_ => Substitute.For<SubstitutablePersistenceReader>())
                    .AddSingleton<IFlatDbManager>(_ =>
                    {
                        IFlatDbManager flatDiff = Substitute.For<IFlatDbManager>();
                        // NSubstitute does not run default interface members, so route the usage overload to the stubbed one.
                        flatDiff.HasStateForBlock(Arg.Any<StateId>(), Arg.Any<ResourcePool.Usage>())
                            .Returns(c => flatDiff.HasStateForBlock(c.ArgAt<StateId>(0)));
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

                                transientResource.ReleaseLease();
                            });

                        flatDiff.GatherSnapshotBundle(Arg.Any<StateId>(), Arg.Any<ResourcePool.Usage>(), Arg.Any<bool>())
                            .Returns(_ => CreateSnapshotBundle(Container.Resolve<ResourcePool>(), Container.Resolve<INodeRecordBuffer>()));
                        flatDiff.GatherReadOnlySnapshotBundle(Arg.Any<StateId>())
                            .Returns(_ => new ReadOnlySnapshotBundle(new SnapshotPooledList(0), Substitute.For<IPersistence.IPersistenceReader>(), false, PersistedSnapshotStack.Empty(), bufferPin: null));
                        flatDiff.HasStateForBlock(Arg.Any<StateId>()).Returns(true);
                        return flatDiff;
                    })
                    .Bind<IFlatCommitTarget, IFlatDbManager>()
                    .AddSingleton<IProcessExitSource>(_ => new CancellationTokenSourceProcessExitSource(_cancellationTokenSource))
                    .AddSingleton<ILogManager>(LimboLogs.Instance)
                    .AddSingleton<IFlatDbConfig>(config)
                    .AddSingleton<IWorldStateScopeProvider.ICodeDb>(_ => new TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb(new TestMemDb()))
                    .AddSingleton<IInitConfig>(_ => Substitute.For<IInitConfig>())
                ;

            if (decorateBuffer)
                _containerBuilder.AddDecorator<INodeRecordBuffer>((_, buffer) => new ForwardingNodeRecordBuffer(buffer));

            if (trieWarmer is not null)
            {
                _containerBuilder.AddSingleton(trieWarmer);
            }

            if (persistenceReader is not null)
            {
                _containerBuilder.AddSingleton(persistenceReader);
            }

            // Externally owned because snapshot bundle take ownership
            _containerBuilder.RegisterType<ReadOnlySnapshotBundle>()
                .WithParameter(new NamedParameter("recordDetailedMetrics", false))
                .WithParameter(new NamedParameter("isHistorical", historical))
                .WithParameter(new NamedParameter("bufferPin", null))
                .WithParameter(TypedParameter.From(ReadOnlySnapshots))
                .WithParameter(TypedParameter.From(PersistedSnapshotStack.Empty()))
                .ExternallyOwned();

            ConfigureSnapshotBundle();
            ConfigureFlatWorldStateScope(stateRoot ?? Keccak.EmptyTreeHash);
        }

        private void ConfigureSnapshotBundle() =>
            _containerBuilder.RegisterType<SnapshotBundle>()
                .SingleInstance()
                .WithParameter(TypedParameter.From(ResourcePool.Usage.MainBlockProcessing))
                .ExternallyOwned();

        private void ConfigureFlatWorldStateScope(Hash256 stateRoot) => _containerBuilder.RegisterType<FlatWorldStateScope>()
                .SingleInstance()
                .WithParameter(TypedParameter.From(new StateId(0, stateRoot)))
                ;

        public FlatWorldStateScope Scope => Container.Resolve<FlatWorldStateScope>();

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();

            LastCommittedSnapshot?.Dispose();
            foreach (Snapshot snapshot in LocalSnapshots) snapshot.Dispose();

            _container?.Dispose();
            _cancellationTokenSource.Dispose();
        }

        public class CancellationTokenSourceProcessExitSource(CancellationTokenSource cancellationTokenSource) : IProcessExitSource
        {
            public CancellationToken Token => cancellationTokenSource.Token;

            public void Exit(int exitCode) => throw new NotImplementedException();
        }

        public void AddSnapshot(Action<SnapshotContent> populator) =>
            ReadOnlySnapshots.Add(FlatTestHelpers.MakeSnapshot(ResourcePool, NodeRecordBuffer, populator));
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
            content.Storages[(testAddress, slotIndex)] = BaseFlatPersistence.DecodeSlotValue(olderSlotValue);
        });

        // Layer 2: Newer snapshot (shadowing Layer 1)
        ctx.AddSnapshot(content =>
        {
            content.Accounts[testAddress] = newerAccount;
            content.Storages[(testAddress, slotIndex)] = BaseFlatPersistence.DecodeSlotValue(newerSlotValue);
        });

        // Layer 3: Another newer snapshot, but only for account
        Account newestAccount = TestItem.GenerateRandomAccount();
        ctx.AddSnapshot(content => content.Accounts[testAddress] = newestAccount);

        // Verify account shadowed by newest snapshot (newestAccount)
        Assert.That(ctx.Scope.Get(testAddress), Is.EqualTo(newestAccount));

        // Verify slot shadowed by Layer 2 snapshot (newerSlotValue)
        IWorldStateScopeProvider.IStorageTree storageTree = ctx.Scope.CreateStorageTree(testAddress);
        storageTree.Get(slotIndex, out UInt256 slotRead186);
        Assert.That(slotRead186, Is.EqualTo(new UInt256(newerSlotValue, isBigEndian: true)));
    }

    [Test]
    public void ClearStorage_empties_the_storage_root([Values] bool historical)
    {
        using TestContext ctx = new(historical: historical);
        Address address = TestItem.AddressA;
        ctx.PersistenceReader.GetAccount(address).Returns(new Account(1, 2, TestItem.KeccakA, Keccak.OfAnEmptyString));
        Assume.That(ctx.Scope.Trieless, Is.EqualTo(historical));
        ctx.Scope.Get(address);
        FlatStorageTree storageTree = (FlatStorageTree)ctx.Scope.CreateStorageTree(address);
        Assume.That(storageTree.RootHash, Is.EqualTo(TestItem.KeccakA));

        storageTree.ClearStorage();

        Assert.That(storageTree.RootHash, Is.EqualTo(Keccak.EmptyTreeHash));
    }

    [Test]
    public void TestAccountAndSlotFromPersistence([Values] bool hasStorage)
    {
        using TestContext ctx = new();

        Address testAddress = TestItem.AddressA;
        UInt256 slotIndex = 1;
        Hash256 expectedRoot = hasStorage ? TestItem.KeccakA : Keccak.EmptyTreeHash;
        Account persistedAccount = new(1, 2, expectedRoot, Keccak.OfAnEmptyString);
        byte[] persistedSlotValue = { 0xDE, 0xAD, 0xBE, 0xEF };

        // Setup Persistence Reader
        ctx.PersistenceReader.GetAccount(testAddress).Returns(persistedAccount);
        UInt256 outValue = BaseFlatPersistence.DecodeSlotValue(persistedSlotValue);
        ctx.PersistenceReader.TryGetSlot(testAddress, slotIndex, ref Arg.Any<UInt256>())
            .Returns(x =>
            {
                x[2] = outValue;
                return true;
            });

        // Verify both are retrieved from persistence
        Assert.That(ctx.Scope.Get(testAddress), Is.EqualTo(persistedAccount));

        IWorldStateScopeProvider.IStorageTree storageTree = ctx.Scope.CreateStorageTree(testAddress);
        Assert.That(storageTree.RootHash, Is.EqualTo(expectedRoot));
        storageTree.Get(slotIndex, out UInt256 slotRead213);
        Assert.That(slotRead213, Is.EqualTo(new UInt256(persistedSlotValue, isBigEndian: true)));
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
            storageBatch.Set(slotIndex, new UInt256(writtenSlotValue, isBigEndian: true));
            storageBatch.Dispose();
        }

        // Verify written items shadow everything else
        Account? resultAccount = scope.Get(testAddress);
        Assert.That(resultAccount!.Balance, Is.EqualTo(testAccount.Balance));
        Assert.That(resultAccount!.Nonce, Is.EqualTo(testAccount.Nonce));

        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(testAddress);
        storageTree.Get(slotIndex, out UInt256 slotRead248);
        Assert.That(slotRead248, Is.EqualTo(new UInt256(writtenSlotValue, isBigEndian: true)));
    }

    [Test]
    public void GetAccount_ReportsWhetherAccountIsInCurrentSnapshot([Values] bool isNull)
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
    public void Commit_DoesNotCarryReadOnlyAccountIntoCommittedSnapshot([Values] bool viaHint)
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;
        Address readOnly = TestItem.AddressA;
        Address written = TestItem.AddressB;
        Account readAccount = TestItem.GenerateIndexedAccount(7);
        ctx.PersistenceReader.GetAccount(readOnly).Returns(readAccount);

        if (viaHint) scope.HintGet(readOnly, readAccount);
        else scope.Get(readOnly);
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            writeBatch.Set(written, TestItem.GenerateIndexedAccount(1));
        }
        scope.Commit(1);

        List<Address> committed = [];
        foreach (KeyValuePair<HashedKey<Address>, Account?> kv in ctx.LastCommittedSnapshot!.Accounts) committed.Add(kv.Key.Key);
        Assert.That(committed, Is.EquivalentTo(new[] { written }));
    }

    [Test]
    public void Get_AfterCommit_ReturnsWriteOfAccountReadEarlierInTheBlock()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;
        Address address = TestItem.AddressA;
        Account before = TestItem.GenerateIndexedAccount(1);
        Account after = TestItem.GenerateIndexedAccount(2);
        ctx.PersistenceReader.GetAccount(address).Returns(before);

        Assert.That(scope.Get(address), Is.EqualTo(before));
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            writeBatch.Set(address, after);
        }
        Assert.That(scope.Get(address), Is.EqualTo(after));
        scope.Commit(1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.Get(address), Is.EqualTo(after));
            ctx.LastCommittedSnapshot!.TryGetAccount(address, out Account? committed);
            Assert.That(committed, Is.EqualTo(after));
        }
    }

    [Test]
    public void Get_AfterCommit_ReadsReadOnlyAccountFromBelowAgain()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;
        Address readOnly = TestItem.AddressA;
        Account first = TestItem.GenerateIndexedAccount(1);
        Account second = TestItem.GenerateIndexedAccount(2);
        ctx.PersistenceReader.GetAccount(readOnly).Returns(first);

        Assert.That(scope.Get(readOnly), Is.EqualTo(first));
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            writeBatch.Set(TestItem.AddressB, TestItem.GenerateIndexedAccount(3));
        }
        scope.Commit(1);
        ctx.PersistenceReader.GetAccount(readOnly).Returns(second);

        Assert.That(scope.Get(readOnly), Is.EqualTo(second));
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
            storageBatch.Set(slotIndex, new UInt256(slotValue, isBigEndian: true));
            storageBatch.Dispose();
        }

        // Commit both
        scope.Commit(1);

        // Verify in snapshot
        Assert.That(ctx.LastCommittedSnapshot, Is.Not.Null);
        ctx.LastCommittedSnapshot!.TryGetAccount(testAddress, out Account? committedAccount);
        Assert.That(committedAccount!.Balance, Is.EqualTo(testAccount.Balance));
        Assert.That(committedAccount!.Nonce, Is.EqualTo(testAccount.Nonce));

        ctx.LastCommittedSnapshot!.TryGetStorage((testAddress, slotIndex), out UInt256? committedSlot);
        Assert.That(committedSlot!.Value.ToMinimalBigEndian(), Is.EqualTo(slotValue));
    }

    [Test]
    public void ApplyBal_WithAccountAndStorageChanges_WithVerifyWithTrie([Values] bool existingAccount, [Values(1, 8)] int accountCount)
    {
        using TestContext ctx = new(config: new FlatDbConfig { VerifyWithTrie = true });
        FlatWorldStateScope scope = ctx.Scope;
        Address[] addresses = new Address[accountCount];
        for (int i = 0; i < accountCount; i++) addresses[i] = Address.FromNumber((UInt256)(i + 1));
        if (existingAccount)
        {
            using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(accountCount);
            foreach (Address address in addresses) writeBatch.Set(address, new Account(nonce: 1, balance: 5));
        }

        byte[] code = [0x60, 0x00];
        ReadOnlyAccountChanges[] changes = new ReadOnlyAccountChanges[accountCount];
        for (int i = 0; i < accountCount; i++)
        {
            changes[i] = Build.An.AccountChanges
                .WithAddress(addresses[i])
                .WithBalanceChanges(new BalanceChange(1, 10))
                .WithNonceChanges(new NonceChange(1, 2))
                .WithCodeChanges(new CodeChange(1, code))
                .WithStorageChanges(1, new StorageChange(1, 0xCAFE))
                .TestObject;
        }
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(changes).TestObject;

        scope.ApplyBal(bal);
        scope.UpdateRootHash();

        foreach (Address address in addresses)
        {
            Account? account = scope.Get(address);
            Assert.That(account, Is.Not.Null);
            scope.CreateStorageTree(address).Get((UInt256)1, out UInt256 value);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(account!.Nonce, Is.EqualTo(2ul));
                Assert.That(account.Balance, Is.EqualTo((UInt256)10));
                Assert.That(account.CodeHash, Is.EqualTo(Keccak.Compute(code)));
                Assert.That(value, Is.EqualTo((UInt256)0xCAFE));
            }
        }
        Assert.That(scope.RootHash, Is.Not.EqualTo(Keccak.EmptyTreeHash));
    }

    [Test]
    public void WriteBatch_DeletingAccountHeldByTrie_WithVerifyWithTrie_DoesNotThrow([Values] bool hasStorage)
    {
        // Deleting an account deletes it from the flat snapshot at once but from the trie only when the batch is
        // disposed. The storage-tree lookup the delete makes in between reads the account back, and with
        // VerifyWithTrie that read used to be compared against the trie, which still held it: the EIP-161
        // clearing of any pre-existing empty account (e.g. the identity precompile at the Spurious Dragon block)
        // threw "Incorrect account ... vs flat:".
        using TestContext ctx = new(config: new FlatDbConfig { VerifyWithTrie = true });
        FlatWorldStateScope scope = ctx.Scope;
        Address address = TestItem.AddressA;
        Account account = hasStorage ? new Account(nonce: 1, balance: 5) : Account.TotallyEmpty;

        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            // storage before the account, as block processing flushes it
            if (hasStorage)
            {
                using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(address, 1);
                storageBatch.Set((UInt256)1, (UInt256)0xCAFE);
            }
            writeBatch.Set(address, account);
        }

        Assert.That(() =>
        {
            using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1);
            writeBatch.Set(address, null);
        }, Throws.Nothing);
        Assert.That(scope.Get(address), Is.Null);
    }

    [Test]
    public void WriteBatch_DeletingEmptyAccount_RecordsAStorageClearThatShadowsItsStorage([Values] bool hasStorage)
    {
        // An EIP-161 deletion only sets the account to null, yet flat history must still record a clear for its
        // storage: an empty account can hold storage, and history readers treat slots older than the clear as dead.
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;
        Address address = TestItem.AddressA;
        ctx.PersistenceReader.GetAccount(address).Returns(new Account(0, 0, hasStorage ? TestItem.KeccakA : Keccak.EmptyTreeHash, Keccak.OfAnEmptyString));

        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            writeBatch.Set(address, null);
        }
        scope.Commit(1);

        Assert.That(ctx.LastCommittedSnapshot!.SelfDestructedStorageAddresses,
            Does.Contain(new KeyValuePair<HashedKey<Address>, bool>(address, !hasStorage)));
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
            content.Storages[(testAddress, slotIndex)] = BaseFlatPersistence.DecodeSlotValue(oldSlotValue);
        });

        // Layer 2: SELFDESTRUCT
        // isNewAccount = false means there was storage to clear
        ctx.AddSnapshot(content => content.SelfDestructedStorageAddresses[testAddress] = false);

        // Layer 3: Empty snapshot after selfdestruct
        ctx.AddSnapshot(content => { });

        // Slot should be blocked by selfdestruct
        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(testAddress);
        storageTree.Get(slotIndex, out UInt256 slotRead387);
        Assert.That(slotRead387, Is.EqualTo(UInt256.Zero));
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
        ctx.AddSnapshot(content => content.Storages[(testAddress, slot1)] = BaseFlatPersistence.DecodeSlotValue(slot1BeforeValue));

        // Snapshot 1: selfdestruct happens at this index
        ctx.AddSnapshot(content => content.SelfDestructedStorageAddresses[testAddress] = false);

        // Snapshot 2: slot2 is set after selfdestruct
        ctx.AddSnapshot(content => content.Storages[(testAddress, slot2)] = BaseFlatPersistence.DecodeSlotValue(slot2AfterValue));

        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(testAddress);

        // slot1 should return zero (blocked by selfdestruct)
        storageTree.Get(slot1, out UInt256 slotRead414);
        Assert.That(slotRead414, Is.EqualTo(UInt256.Zero));

        // slot2 should return the value (written after selfdestruct)
        storageTree.Get(slot2, out UInt256 slotRead417);
        Assert.That(slotRead417, Is.EqualTo(new UInt256(slot2AfterValue, isBigEndian: true)));
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
            storageBatch.Set(slotIndex, new UInt256(slotValue, isBigEndian: true));
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
            storageBatch.Set(slot1, new UInt256(value1, isBigEndian: true));
            storageBatch.Set(slot2, new UInt256(value2, isBigEndian: true));
            storageBatch.Set(slot3, new UInt256(value3, isBigEndian: true));
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
    public void StorageRootAfterParallelCommitMatchesRawTrie([Values(1, 2, 4)] int concurrency)
    {
        using ParallelUnbalancedWork.WorkerScope workers = ParallelUnbalancedWork.BeginWorkerScope(concurrency);
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
                for (int i = firstSlot; i < lastSlot; i++) storageBatch.Set((UInt256)i, new UInt256([(byte)i, (byte)(i >> 8)], isBigEndian: true));
            }

            scope.Commit((ulong)(commit + 1));
        }

        TestMemDb testDb = new();
        RawScopedTrieStore trieStore = new(testDb);
        StorageTree expectedTree = new(trieStore, LimboLogs.Instance);
        for (int i = 1; i <= slotsPerCommit * commitCount; i++) expectedTree.Set((UInt256)i, new UInt256([(byte)i, (byte)(i >> 8)], isBigEndian: true).ToMinimalBigEndian());
        expectedTree.UpdateRootHash();

        Account? account = scope.Get(address);
        Assert.That(account, Is.Not.Null);
        Assert.That(account!.StorageRoot, Is.EqualTo(expectedTree.RootHash));
    }

    [Test]
    [NonParallelizable]
    public void EarlyStorageApply_ReconcilesFinalWrites([Values] bool applyEarly, [Values] bool clearAtBlockEnd, [Values] bool deferStorageTrieCommit)
    {
        using IDisposable gap = SetMinIdleGap(TimeSpan.Zero);
        using TestContext ctx = new(config: new FlatDbConfig { ApplyStorageWritesOnIdleThread = applyEarly, DeferStorageTrieCommit = deferStorageTrieCommit });
        FlatWorldStateScope scope = ctx.Scope;
        Address address = TestItem.AddressA;
        ctx.PersistenceReader.GetAccount(address).Returns(Build.An.Account.WithBalance(1).TestObject);
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(address, 2);
            storage.Set(2, 10);
            storage.Set(3, 30);
        }
        scope.Commit(1);
        FlatStorageTree tree = (FlatStorageTree)scope.CreateStorageTree(address);
        Hash256 preBlockRoot = tree.RootHash;
        UInt256[] expected = new UInt256[40];
        expected[3] = clearAtBlockEnd ? UInt256.Zero : (UInt256)30;

        if (applyEarly)
        {
            ApplyEarlyHints(tree, () =>
            {
                for (int slot = 0; slot < expected.Length; slot++) tree.HintSet((UInt256)slot, (UInt256)(slot + 1));
                tree.HintSet(1, 1000);
                tree.HintSet(2, 0);
            });
            Assert.That(tree.RootHash, Is.EqualTo(preBlockRoot), "private updates must not change the owner root");
        }

        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(address, expected.Length);
            if (clearAtBlockEnd) storage.Clear();
            for (int slot = 0; slot < expected.Length; slot++)
            {
                if (slot == 3) continue;
                expected[slot] = slot == 1 ? 2000 : slot == 2 ? 0 : (UInt256)(slot + 1);
                storage.Set((UInt256)slot, expected[slot]);
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.EarlyApplyCounts.Reused, Is.EqualTo(applyEarly && !clearAtBlockEnd ? expected.Length - 2 : 0));
            Assert.That(scope.EarlyApplyCounts.Restored, Is.EqualTo(applyEarly && !clearAtBlockEnd ? 1 : 0));
            Assert.That(scope.EarlyApplyCounts.Abandoned, Is.Zero);
        }
        scope.Commit(2);
        AssertStorageRoot(ctx, expected);
    }

    [Test]
    [NonParallelizable]
    public void EarlyStorageApply_AdoptsAllMatchingWritesAndPublishesNodes()
    {
        using IDisposable gap = SetMinIdleGap(TimeSpan.Zero);
        using TestContext ctx = new();
        Address address = TestItem.AddressA;
        ctx.PersistenceReader.GetAccount(address).Returns(Build.An.Account.WithBalance(1).TestObject);
        FlatStorageTree tree = (FlatStorageTree)ctx.Scope.CreateStorageTree(address);
        ApplyEarlyHints(tree, () => tree.HintSet(1, 123));

        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = ctx.Scope.StartWriteBatch(1))
        {
            using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(address, 1);
            storage.Set(1, 123);
        }
        Assert.That(ctx.Scope.EarlyApplyCounts.Reused, Is.EqualTo(1));
        ctx.Scope.Commit(1);
        AssertStorageRoot(ctx, [0, 123]);
        Assert.That(ctx.LastCommittedSnapshot!.TryGetStorageNode((address.ToAccountPath.ToHash256(), TreePath.Empty), out _), Is.True,
            "adoption must publish the private root even when the final batch hashes nothing");
        Assert.That(ctx.LastCommittedSnapshot.StorageNodes, Is.Not.Empty, "the boundary precedes adopted idle-thread writes");
    }

    [Test]
    [NonParallelizable]
    public void EarlyStorageApply_UnfinishedPassOutlivesCommitOrDispose([Values] bool disposeScope, [Values] bool persistedRoot)
    {
        using IDisposable gap = SetMinIdleGap(TimeSpan.Zero);
        Address address = TestItem.AddressA;
        Account account = Build.An.Account.WithBalance(1).TestObject;
        Hash256? stateRoot = null;
        Dictionary<(Hash256? Storage, TreePath Path), byte[]> persistedNodes = [];
        if (persistedRoot)
        {
            RawTrieStore store = new(new TestMemDb());
            StorageTree storage = new(store.GetTrieStore(address.ToAccountPath.ToHash256()), LimboLogs.Instance);
            storage.Set(1, ((UInt256)10).ToMinimalBigEndian());
            storage.Commit();
            account = account.WithChangedStorageRoot(storage.RootHash);
            StateTree state = new(store.GetTrieStore(null), LimboLogs.Instance);
            state.Set(address, account);
            state.Commit();
            stateRoot = state.RootHash;
            persistedNodes = CollectNodes(store.GetTrieStore(null), stateRoot).Rlps;
        }

        using TestContext ctx = new(config: new FlatDbConfig(), stateRoot: stateRoot);
        ctx.PersistenceReader.GetAccount(address).Returns(account);
        int backgroundLoads = 0;
        ctx.PersistenceReader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>())
            .Returns(c => persistedNodes.GetValueOrDefault((null, c.ArgAt<TreePath>(0))));
        ctx.PersistenceReader.TryLoadStorageRlp(Arg.Any<Hash256>(), Arg.Any<TreePath>(), Arg.Any<ReadFlags>())
            .Returns(c =>
            {
                if (Thread.CurrentThread.Name == "Storage early apply") Interlocked.Increment(ref backgroundLoads);
                return persistedNodes.GetValueOrDefault((c.ArgAt<Hash256>(0), c.ArgAt<TreePath>(1)));
            });
        FlatWorldStateScope scope = ctx.Scope;
        if (!persistedRoot)
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
            {
                using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(address, 1);
                storage.Set(1, 10);
            }
            scope.Commit(1);
            // The root predates the worker's active generation, so retaining only its new writes is insufficient.
            for (int i = 0; i < 10; i++) ctx.NodeRecordBuffer.StartWriteBatch(64 * 1024).Dispose();
        }

        FlatStorageTree tree = (FlatStorageTree)scope.CreateStorageTree(address);
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim resume = new();
        tree.OnEarlyPassDrained = () => { started.Set(); resume.Wait(); };
        try
        {
            tree.HintSet(1, 20);
            Assert.That(started.Wait(5000), Is.True);
            Task owner = Task.Run(() =>
            {
                if (!disposeScope)
                {
                    using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
                    {
                        using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(address, 1);
                        storage.Set(1, 30);
                    }
                    Assert.That(scope.EarlyApplyCounts.Abandoned, Is.EqualTo(1));
                    // The commit target retires the transient synchronously; a worker transient lease would block here.
                    scope.Commit(2);
                    AssertStorageRoot(ctx, [0, 30]);
                }
                scope.Dispose();
                ctx.LastCommittedSnapshot?.Dispose();
                ctx.LastCommittedSnapshot = null;
            });
            Assert.That(owner.Wait(5000), Is.True, "commit/dispose must not wait for an unfinished pass");
            ctx.PersistenceReader.DidNotReceive().Dispose();
            for (int i = 0; i < 10; i++) ctx.NodeRecordBuffer.StartWriteBatch(64 * 1024).Dispose();
        }
        finally
        {
            resume.Set();
        }
        // The pass disposes its source last.
        Assert.That(() => ctx.PersistenceReader.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IDisposable.Dispose)), Is.EqualTo(1).After(5000, 10));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.EarlyApplyCounts.Applied, Is.EqualTo(1), "the detached pass must finish after bundle teardown");
            Assert.That(backgroundLoads, persistedRoot ? Is.Positive : Is.Zero);
        }
    }

    [Test]
    [NonParallelizable]
    public void EarlyStorageApply_FailedPassIsNotAdopted()
    {
        using IDisposable gap = SetMinIdleGap(TimeSpan.Zero);
        using TestContext ctx = new();
        Address address = TestItem.AddressA;
        ctx.PersistenceReader.GetAccount(address).Returns(Build.An.Account.WithBalance(1).TestObject);
        FlatStorageTree tree = (FlatStorageTree)ctx.Scope.CreateStorageTree(address);
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim resume = new();
        tree.OnEarlyPassDrained = () =>
        {
            started.Set();
            resume.Wait();
            throw new InvalidOperationException("failed early pass");
        };
        try
        {
            tree.HintSet(1, 123);
            Assert.That(started.Wait(5000), Is.True);
        }
        finally
        {
            resume.Set();
        }
        Assert.That(() => tree.EarlyWritesDrained, Is.True.After(5000, 10));

        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = ctx.Scope.StartWriteBatch(1))
        {
            using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(address, 1);
            storage.Set(1, 321);
        }
        Assert.That(ctx.Scope.EarlyApplyCounts, Is.EqualTo((0, 0, 0, 0)));
        ctx.Scope.Commit(1);
        AssertStorageRoot(ctx, [0, 321]);
        ctx.Scope.Dispose();
        ctx.PersistenceReader.Received(1).Dispose();
    }

    [Test]
    [NonParallelizable]
    public void EarlyStorageApply_WithoutFinalStorageBatchDoesNotPublish()
    {
        using IDisposable gap = SetMinIdleGap(TimeSpan.Zero);
        using TestContext ctx = new();
        Address address = TestItem.AddressA;
        ctx.PersistenceReader.GetAccount(address).Returns(Build.An.Account.WithBalance(1).TestObject);
        FlatStorageTree tree = (FlatStorageTree)ctx.Scope.CreateStorageTree(address);
        ApplyEarlyHints(tree, () => tree.HintSet(1, 123));

        ctx.Scope.Commit(1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctx.Scope.Get(address)!.StorageRoot, Is.EqualTo(Keccak.EmptyTreeHash));
            Assert.That(ctx.LastCommittedSnapshot!.TryGetStorageNode((address.ToAccountPath.ToHash256(), TreePath.Empty), out _), Is.False);
            Assert.That(ctx.LastCommittedSnapshot.StorageNodes, Is.Empty, "unreachable speculative writes are not snapshot changes");
        }
        ctx.Scope.Dispose();
        ctx.PersistenceReader.Received(1).Dispose();
    }

    [Test]
    [NonParallelizable]
    public void EarlyStorageApply_EligibilityAndBackToBackSuppression()
    {
        using IDisposable gap = SetMinIdleGap(TimeSpan.Zero);
        using TestContext enabled = new();
        using TestContext disabled = new(config: new FlatDbConfig { ApplyStorageWritesOnIdleThread = false });
        using TestContext verifying = new(config: new FlatDbConfig { VerifyWithTrie = true });
        using TestContext historical = new(historical: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(enabled.Scope.AppliesStorageWritesEarly, Is.True);
            Assert.That(disabled.Scope.AppliesStorageWritesEarly, Is.False);
            Assert.That(verifying.Scope.AppliesStorageWritesEarly, Is.False);
            Assert.That(historical.Scope.AppliesStorageWritesEarly, Is.False);
        }
        IdleStorageApplier.MinIdleGap = TimeSpan.FromHours(1);
        enabled.Scope.Commit(1);
        Assert.That(enabled.Scope.AppliesStorageWritesEarly, Is.False);
        using TestContext next = new();
        Assert.That(next.Scope.AppliesStorageWritesEarly, Is.False);
    }

    private static void ApplyEarlyHints(FlatStorageTree tree, Action hint)
    {
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim resume = new();
        tree.OnEarlyPassDrained = () => { started.Set(); resume.Wait(); };
        try
        {
            hint();
            Assert.That(started.Wait(5000), Is.True);
        }
        finally
        {
            resume.Set();
            Assert.That(() => tree.EarlyWritesDrained, Is.True.After(5000, 10));
            tree.OnEarlyPassDrained = null;
        }
    }

    private static void AssertStorageRoot(TestContext context, UInt256[] values)
    {
        StorageTree expected = new(new RawScopedTrieStore(new TestMemDb()), LimboLogs.Instance);
        for (int slot = 0; slot < values.Length; slot++)
        {
            if (!values[slot].IsZero) expected.Set((UInt256)slot, values[slot].ToMinimalBigEndian());
        }
        expected.UpdateRootHash();
        Assert.That(context.Scope.Get(TestItem.AddressA)!.StorageRoot, Is.EqualTo(expected.RootHash));
    }

    private static IDisposable SetMinIdleGap(TimeSpan gap)
    {
        TimeSpan previous = IdleStorageApplier.MinIdleGap;
        IdleStorageApplier.MinIdleGap = gap;
        return new Reactive.AnonymousDisposable(() => IdleStorageApplier.MinIdleGap = previous);
    }

    [Test]
    public void DeferredStorageTrieCommit_CommitsTheSameStorageNodes([Values(1, 3)] int contractCount, [Values] bool clearInSecondBatch)
    {
        (HashSet<string> nodesInBatch, Hash256[] rootsInBatch) = CommitStorageSlots(deferStorageTrieCommit: false, contractCount, clearInSecondBatch);
        (HashSet<string> nodesAtCommit, Hash256[] rootsAtCommit) = CommitStorageSlots(deferStorageTrieCommit: true, contractCount, clearInSecondBatch);

        Assert.That(nodesAtCommit, Is.Not.Empty);
        Assert.That(nodesAtCommit, Is.EquivalentTo(nodesInBatch));
        Assert.That(rootsAtCommit, Is.EqualTo(rootsInBatch));
    }

    [Test]
    public void DeferredStorageTrieCommit_NextBlockBuildsOnTheCommittedNodes([Values] bool deferStorageTrieCommit, [Values] bool clearInSecondBatch)
    {
        const int slotCount = 64;
        using TestContext ctx = new(config: new FlatDbConfig { DeferStorageTrieCommit = deferStorageTrieCommit });
        FlatWorldStateScope scope = ctx.Scope;
        Address[] addresses = [TestItem.AddressA, TestItem.AddressB];
        foreach (Address address in addresses) ctx.PersistenceReader.GetAccount(address).Returns(TestItem.GenerateRandomAccount());
        UInt256[] expected = new UInt256[slotCount + 1];

        // Block 1: two batches, the second overwriting half of the first, as per-transaction root commits do. Clearing
        // first drops the other half too, as a self-destruct and recreation in a later transaction does.
        for (int batch = 0; batch < 2; batch++)
        {
            using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(addresses.Length);
            foreach (Address address in addresses)
            {
                using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(address, slotCount);
                if (batch == 1 && clearInSecondBatch) storageBatch.Clear();
                for (int slot = batch * slotCount / 2; slot < slotCount; slot++)
                {
                    expected[slot] = (UInt256)(slot + 1 + batch * 1000);
                    storageBatch.Set((UInt256)slot, expected[slot]);
                }
            }
        }
        if (clearInSecondBatch) Array.Clear(expected, 0, slotCount / 2);
        scope.Commit(1);

        // Block 2 updates the tries block 1 committed, so a node block 1 left out fails to resolve here.
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(addresses.Length))
        {
            foreach (Address address in addresses)
            {
                using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(address, 2);
                expected[0] = 7;
                expected[slotCount] = 9;
                storageBatch.Set(0, expected[0]);
                storageBatch.Set((UInt256)slotCount, expected[slotCount]);
            }
        }
        scope.Commit(2);

        StorageTree expectedTree = new(new RawScopedTrieStore(new TestMemDb()), LimboLogs.Instance);
        for (int slot = 0; slot <= slotCount; slot++) expectedTree.Set((UInt256)slot, expected[slot].ToMinimalBigEndian());
        expectedTree.UpdateRootHash();
        foreach (Address address in addresses)
        {
            Assert.That(scope.Get(address)!.StorageRoot, Is.EqualTo(expectedTree.RootHash));
        }
    }

    private static (HashSet<string> Nodes, Hash256[] Roots) CommitStorageSlots(bool deferStorageTrieCommit, int contractCount, bool clearInSecondBatch)
    {
        const int slotCount = 64;
        using TestContext ctx = new(config: new FlatDbConfig { DeferStorageTrieCommit = deferStorageTrieCommit });
        FlatWorldStateScope scope = ctx.Scope;
        Address[] addresses = [TestItem.AddressA, TestItem.AddressB, TestItem.AddressC];
        addresses = addresses[..contractCount];
        foreach (Address address in addresses) ctx.PersistenceReader.GetAccount(address).Returns(TestItem.GenerateRandomAccount());

        // A second batch that clears first drops the trie the first one built, as a self-destruct and recreation in a
        // later transaction does, so none of that trie's nodes may reach the snapshot.
        int batchCount = clearInSecondBatch ? 2 : 1;
        for (int batch = 0; batch < batchCount; batch++)
        {
            using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(addresses.Length);
            for (int contract = 0; contract < addresses.Length; contract++)
            {
                using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(addresses[contract], slotCount);
                if (batch > 0) storageBatch.Clear();
                for (int slot = batch * slotCount / 2; slot < slotCount; slot++) storageBatch.Set((UInt256)slot, (UInt256)(slot + 1 + contract * 100 + batch * 1000));
            }
        }
        scope.Commit(1);

        HashSet<string> nodes = [];
        foreach ((Hash256 address, TreePath path, _, NodeRecord record) in ctx.LastCommittedSnapshot!.StorageNodes)
        {
            nodes.Add($"{address}/{path}/{record.Rlp.ToHexString()}");
        }

        Hash256[] roots = new Hash256[addresses.Length];
        for (int contract = 0; contract < addresses.Length; contract++) roots[contract] = scope.Get(addresses[contract])!.StorageRoot;
        return (nodes, roots);
    }

    [Test]
    public void Committed_roots_and_published_nodes_match_patricia_tree(
        [Values(0, 20)] int blocksBetweenCommits,
        [Values] bool deferStorageTrieCommit,
        // 512 crosses the bulk set's parallel threshold.
        [Values(48, 512)] int slotsPerContract)
    {
        const int contractCount = 8;
        FlatDbConfig config = new() { DeferStorageTrieCommit = deferStorageTrieCommit };
        Account baseAccount = new(1, 1);

        RawTrieStore expectedStore = new(new TestMemDb());
        StateTree expectedState = new(expectedStore.GetTrieStore(null), LimboLogs.Instance);
        StorageTree[] expectedStorages = new StorageTree[contractCount];
        for (int contract = 0; contract < contractCount; contract++)
        {
            expectedStorages[contract] = new StorageTree(expectedStore.GetTrieStore(TestItem.Addresses[contract].ToAccountPath.ToHash256()), LimboLogs.Instance);
            for (int slot = 0; slot < 2 * slotsPerContract; slot++) expectedStorages[contract].Set((UInt256)slot, ((UInt256)(slot + 1)).ToMinimalBigEndian());
            expectedStorages[contract].Commit();
            expectedState.Set(TestItem.Addresses[contract], baseAccount.WithChangedStorageRoot(expectedStorages[contract].RootHash));
        }
        expectedState.Commit();

        // The tries start from nodes only persistence holds, so the blocks load and warm nodes from outside the trie node buffer.
        Dictionary<(Hash256? Storage, TreePath Path), byte[]> persistedNodes = CollectNodes(expectedStore.GetTrieStore(null), expectedState.RootHash).Rlps;
        InlineTrieWarmer warmer = new();
        using TestContext ctx = new(config: config, trieWarmer: warmer, stateRoot: expectedState.RootHash);
        ctx.PersistenceReader.GetAccount(Arg.Any<Address>()).Returns(baseAccount);
        for (int contract = 0; contract < contractCount; contract++)
        {
            ctx.PersistenceReader.GetAccount(TestItem.Addresses[contract]).Returns(baseAccount.WithChangedStorageRoot(expectedStorages[contract].RootHash));
        }
        ctx.PersistenceReader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>())
            .Returns(c => persistedNodes.GetValueOrDefault((null, c.ArgAt<TreePath>(0))));
        ctx.PersistenceReader.TryLoadStorageRlp(Arg.Any<Hash256>(), Arg.Any<TreePath>(), Arg.Any<ReadFlags>())
            .Returns(c => persistedNodes.GetValueOrDefault((c.ArgAt<Hash256>(0), c.ArgAt<TreePath>(1))));
        FlatWorldStateScope scope = ctx.Scope;
        warmer.Scope = scope;

        int lastBlock = blocksBetweenCommits + 2;
        // The first and last blocks write every contract and the ones in between one each, so the last block
        // updates tries committed many blocks, and buffer generations, before.
        (int First, int End) ContractsOf(int block) =>
            block == 1 || block == lastBlock ? (0, contractCount) : (block % contractCount, block % contractCount + 1);
        UInt256 SlotOf(int block, int i) => (UInt256)((block * 7 + i) % (2 * slotsPerContract));

        for (int block = 1; block <= lastBlock; block++)
        {
            // Warming the next block's slots fetches the persisted nodes that block then reads from the trie node cache.
            if (block < lastBlock)
            {
                (int nextFirstContract, int nextEndContract) = ContractsOf(block + 1);
                for (int contract = nextFirstContract; contract < nextEndContract; contract++)
                {
                    for (int i = 0; i < slotsPerContract; i++) scope.HintWarmSlot(TestItem.Addresses[contract], SlotOf(block + 1, i));
                }
            }

            (int firstContract, int endContract) = ContractsOf(block);
            Address plainAddress = TestItem.Addresses[contractCount + block];
            Account plainAccount = new((ulong)block, (UInt256)block);
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(endContract - firstContract + 1))
            {
                for (int contract = firstContract; contract < endContract; contract++)
                {
                    using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(TestItem.Addresses[contract], slotsPerContract);
                    for (int i = 0; i < slotsPerContract; i++)
                    {
                        UInt256 slot = SlotOf(block, i);
                        UInt256 value = i % 5 == 0 ? UInt256.Zero : (UInt256)(block * 1000 + i);
                        storageBatch.Set(slot, value);
                        expectedStorages[contract].Set(slot, value.ToMinimalBigEndian());
                    }
                }

                writeBatch.Set(plainAddress, plainAccount);
            }
            scope.Commit((ulong)block);

            for (int contract = firstContract; contract < endContract; contract++)
            {
                expectedStorages[contract].Commit();
                expectedState.Set(TestItem.Addresses[contract], baseAccount.WithChangedStorageRoot(expectedStorages[contract].RootHash));
            }
            expectedState.Set(plainAddress, plainAccount);
            expectedState.Commit();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(scope.RootHash, Is.EqualTo(expectedState.RootHash), $"state root of block {block}");
                for (int contract = firstContract; contract < endContract; contract++)
                {
                    Assert.That(scope.Get(TestItem.Addresses[contract])!.StorageRoot, Is.EqualTo(expectedStorages[contract].RootHash), $"storage root of contract {contract} in block {block}");
                }
            }
        }

        Assert.That(CollectNodes(new StateTrieStoreAdapter(ctx.SnapshotBundle), scope.RootHash).Nodes,
            Is.EquivalentTo(CollectNodes(expectedStore.GetTrieStore(null), expectedState.RootHash).Nodes));
    }

    [Test]
    public void TryLoadTrieNodeRecord_reads_persisted_without_warming_and_skips_in_memory_snapshot_nodes([Values] bool storage)
    {
        ResourcePool pool = new(new FlatDbConfig());
        using INodeRecordBuffer buffer = FlatTestHelpers.NewTestBuffer();
        Hash256? address = storage ? TestItem.KeccakA : null;
        TreePath persistedPath = TreePath.FromHexString("12");
        TreePath readOnlySnapshotPath = TreePath.FromHexString("34");
        TreePath ownSnapshotPath = TreePath.FromHexString("56");
        byte[] persistedRlp = FlatTestHelpers.LeafRlp(0x01);
        byte[] readOnlySnapshotRlp = FlatTestHelpers.LeafRlp(0x02);
        byte[] ownSnapshotRlp = FlatTestHelpers.LeafRlp(0x03);

        Snapshot SnapshotWithNode(TreePath path, byte[] rlp) => FlatTestHelpers.MakeSnapshot(pool, buffer, content =>
        {
            if (address is null) content.SetStateNodeRlp(buffer, path, rlp);
            else content.SetStorageNodeRlp(buffer, address, path, rlp);
        });

        SinglePersistedNodeReader reader = new(persistedPath, persistedRlp);
        using SnapshotBundle bundle = new(
            new ReadOnlySnapshotBundle(FlatTestHelpers.SnapshotList(SnapshotWithNode(readOnlySnapshotPath, readOnlySnapshotRlp)), reader, false, PersistedSnapshotStack.Empty(), bufferPin: null),
            buffer,
            pool,
            ResourcePool.Usage.MainBlockProcessing,
            FlatTestHelpers.SnapshotList(SnapshotWithNode(ownSnapshotPath, ownSnapshotRlp)));

        byte[]? Load(TreePath path, byte[] rlp) =>
            FlatTestHelpers.LoadRlp((action, nodes) => bundle.TryLoadTrieNodeRecord(address, path, action, nodes)) is { } loaded && ValueKeccak.Compute(loaded) == ValueKeccak.Compute(rlp) ? loaded : null;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Load(persistedPath, persistedRlp), Is.EqualTo(persistedRlp), "persisted");
            Assert.That(Load(persistedPath, persistedRlp), Is.EqualTo(persistedRlp), "persisted again");
            Assert.That(reader.Loads, Is.EqualTo(2), "a loaded node is not warmed");
            Assert.That(Load(readOnlySnapshotPath, readOnlySnapshotRlp), Is.Null, "read-only bundle snapshot must be read through the buffer");
            Assert.That(Load(ownSnapshotPath, ownSnapshotRlp), Is.Null, "own snapshot must be read through the buffer");
            Assert.That(Load(ownSnapshotPath, readOnlySnapshotRlp), Is.Null, "snapshot node of another hash");
        }
    }

    [Test]
    public void Retained_snapshot_roots_are_read_and_updated_from_the_buffer_after_rotation(
        [Values] bool storage, [Values] bool readOnlySnapshots)
    {
        const int keyCount = 48;
        const int versionCount = 3;
        ResourcePool pool = new(new FlatDbConfig());
        using NodeRecordBuffer buffer = new(1024 * 1024, 64 * 1024, NodeRecordBuffer.DefaultLiveGenerations);
        Hash256? address = storage ? TestItem.KeccakA : null;
        byte[] oldRlp = FlatTestHelpers.LeafRlp(0x01);
        ulong oldPointer = buffer.AppendRecordFromRlp(address, TreePath.FromHexString("f"), ValueKeccak.Compute(oldRlp), oldRlp, []);
        FlatTestHelpers.StartNewGeneration(buffer);

        ValueHash256[] keys = new ValueHash256[keyCount];
        for (int i = 0; i < keys.Length; i++) keys[i] = ValueKeccak.Compute($"snapshot-key-{i}");
        (ValueHash256 Hash, ulong Pointer)[] roots = new (ValueHash256 Hash, ulong Pointer)[versionCount];
        byte[][][] values = new byte[versionCount][][];
        SnapshotPooledList snapshots = new(versionCount);
        SinglePersistedNodeReader reader = new(TreePath.FromHexString("abcdef"), oldRlp);
        using (SnapshotBundle writer = new(
            new ReadOnlySnapshotBundle(new SnapshotPooledList(0), new NoopPersistenceReader(), false, PersistedSnapshotStack.Empty(), bufferPin: null),
            buffer, pool, ResourcePool.Usage.MainBlockProcessing))
        {
            (ValueHash256 Hash, ulong Pointer) root = (ValueKeccak.EmptyTreeHash, 0);
            for (int version = 0; version < versionCount; version++)
            {
                INodeRecordBufferPin retain = buffer.PinHead();
                NodeRecordTrie trie = new(writer, address, root);
                using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(keyCount);
                values[version] = version == 0 ? new byte[keyCount][] : (byte[][])values[version - 1].Clone();
                for (int i = 0; i < (version == 0 ? keyCount : 1); i++)
                {
                    byte[] value = new byte[32];
                    value.AsSpan().Fill((byte)(i + version + 1));
                    values[version][i] = value;
                    entries.Add(new PatriciaTree.BulkSetEntry(keys[i], value));
                }

                trie.BulkSet(entries, canParallelize: false);
                root = roots[version] = trie.Root;
                trie.Publish();
                writer.CollectAndApplySnapshot(new StateId((ulong)version, Keccak.EmptyTreeHash), new StateId((ulong)version + 1, trie.RootHash), retain, returnSnapshot: false);
                Snapshot snapshot = writer._snapshots[version];
                snapshot.AcquireLease();
                snapshots.Add(snapshot);
                FlatTestHelpers.StartNewGeneration(buffer);
            }
        }

        using SnapshotBundle bundle = new(
            new ReadOnlySnapshotBundle(readOnlySnapshots ? snapshots : new SnapshotPooledList(0), reader, false, PersistedSnapshotStack.Empty(), bufferPin: null),
            buffer, pool, ResourcePool.Usage.MainBlockProcessing,
            readOnlySnapshots ? null : snapshots);
        for (int i = 0; i < 12; i++) FlatTestHelpers.StartNewGeneration(buffer);
        Assert.That(() => buffer.FindBloom(oldPointer) is null, Is.True.After(10_000, 10), "the unretained generation is compacted while snapshots retain later generations");

        using INodeRecordBufferPin writeRetain = buffer.PinHead();
        for (int version = 0; version < versionCount; version++)
        {
            Assert.That(buffer.FindRoot(address, roots[version].Hash, ulong.MaxValue), Is.EqualTo(roots[version].Pointer), $"indexed root {version}");
            NodeRecordTrie trie = new(bundle, address, (roots[version].Hash, buffer.FindRoot(address, roots[version].Hash, ulong.MaxValue)));
            using (Assert.EnterMultipleScope())
            {
                for (int i = 0; i < keys.Length; i++) Assert.That(trie.TryGet(keys[i]), Is.EqualTo(values[version][i]), $"version {version}, key {i}");
            }

            byte[] updated = new byte[32];
            updated.AsSpan().Fill(0xff);
            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(1);
            entries.Add(new PatriciaTree.BulkSetEntry(keys[^1], updated));
            trie.BulkSet(entries, canParallelize: false);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(trie.TryGet(keys[^1]), Is.EqualTo(updated), $"updated version {version}");
                Assert.That(trie.TryGet(keys[0]), Is.EqualTo(values[version][0]), $"unchanged subtree of version {version}");
            }
        }

        Assert.That(reader.Loads, Is.Zero, "in-memory nodes must resolve through the retained buffer, without source fetches");
    }

    /// <summary>The hash-referenced nodes of the state trie at <paramref name="root"/> and its storage tries, by address hash, path and RLP.</summary>
    private static NodeCollector CollectNodes(IScopedTrieStore store, Hash256 root)
    {
        NodeCollector collector = new();
        new PatriciaTree(store, LimboLogs.Instance).Accept(collector, root);
        return collector;
    }

    private sealed class NodeCollector : ITreeVisitor<TreePathContextWithStorage>
    {
        public HashSet<string> Nodes { get; } = [];

        public Dictionary<(Hash256? Storage, TreePath Path), byte[]> Rlps { get; } = [];

        public bool IsFullDbScan => false;

        public bool ShouldVisit(in TreePathContextWithStorage nodeContext, in ValueHash256 nextNode) => true;

        public void VisitTree(in TreePathContextWithStorage nodeContext, in ValueHash256 rootHash) { }

        public void VisitMissingNode(in TreePathContextWithStorage nodeContext, in ValueHash256 nodeHash) =>
            throw new TrieException($"Missing node {nodeHash} at {nodeContext.Storage}/{nodeContext.Path}");

        public void VisitBranch(in TreePathContextWithStorage nodeContext, TrieNode node) => Add(nodeContext, node);

        public void VisitExtension(in TreePathContextWithStorage nodeContext, TrieNode node) => Add(nodeContext, node);

        public void VisitLeaf(in TreePathContextWithStorage nodeContext, TrieNode node) => Add(nodeContext, node);

        public void VisitAccount(in TreePathContextWithStorage nodeContext, TrieNode node, in AccountStruct account) { }

        private void Add(in TreePathContextWithStorage nodeContext, TrieNode node)
        {
            if (node.FullRlp.Length >= Hash256.Size || nodeContext.Path.Length == 0)
            {
                byte[] rlp = node.FullRlp.ToArray()!;
                Nodes.Add($"{nodeContext.Storage}/{nodeContext.Path}/{rlp.ToHexString()}");
                Rlps[(nodeContext.Storage, nodeContext.Path)] = rlp;
            }
        }
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
            storageBatch.Set(slot1, new UInt256(value1, isBigEndian: true));
            storageBatch.Dispose();
        }
        scope.Commit(1);

        // Second commit - set slot2
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(testAddress, 1);
            storageBatch.Set(slot2, new UInt256(value2, isBigEndian: true));
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
            storageBatch.Set(slot1, new UInt256(value1, isBigEndian: true));
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
            storageBatch.Set(slot2, new UInt256(value2, isBigEndian: true));
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
    public void StorageClear_RemovesSlotsWrittenAfterAnEarlierClearInTheSameBlock([Values] bool setInTheClearingBatch)
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        Address testAddress = TestItem.AddressA;
        UInt256 slot1 = 1;
        UInt256 slot2 = 2;
        UInt256 value = 0xAA;
        ctx.PersistenceReader.GetAccount(testAddress).Returns(TestItem.GenerateRandomAccount());

        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        using (IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(testAddress, 1))
        {
            storageBatch.Set(slot1, value);
        }

        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        using (IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(testAddress, 1))
        {
            storageBatch.Clear();
            if (setInTheClearingBatch) storageBatch.Set(slot2, value);
        }

        if (!setInTheClearingBatch)
        {
            using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1);
            using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(testAddress, 1);
            storageBatch.Set(slot2, value);
        }

        // The slot written after the first clear must still be found by the second one.
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        using (IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(testAddress, 0))
        {
            storageBatch.Clear();
        }

        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(testAddress);
        storageTree.Get(slot1, out UInt256 slot1Value);
        storageTree.Get(slot2, out UInt256 slot2Value);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(slot1Value, Is.EqualTo(UInt256.Zero));
            Assert.That(slot2Value, Is.EqualTo(UInt256.Zero));
        }
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
    public void PausedScopeKeepsEarlierWritesButOmitsInterleavedScopeNodes()
    {
        using TestContext ctx = new();
        IWorldStateScopeProvider provider = ctx.WorldStateManager.GlobalWorldState;
        Assert.That(provider.TryBeginScope(null, new LocalMetrics(), out IWorldStateScopeProvider.IScope? paused), Is.True);
        using IWorldStateScopeProvider.IScope pausedScope = paused!;
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = pausedScope.StartWriteBatch(2))
        {
            batch.Set(TestItem.AddressA, new Account(1, 100));
            batch.Set(TestItem.AddressB, new Account(2, 200));
        }

        Assert.That(provider.TryBeginScope(null, new LocalMetrics(), out IWorldStateScopeProvider.IScope? interleaved), Is.True);
        using IWorldStateScopeProvider.IScope interleavedScope = interleaved!;
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = interleavedScope.StartWriteBatch(1))
            batch.Set(TestItem.AddressD, new Account(4, 400));
        interleavedScope.Commit(1);
        Snapshot interleavedSnapshot = ctx.LastCommittedSnapshot!;
        interleavedSnapshot.AcquireLease();
        using Snapshot interleavedLease = interleavedSnapshot;

        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = pausedScope.StartWriteBatch(1))
            batch.Set(TestItem.AddressC, new Account(3, 300));
        pausedScope.Commit(1);
        Snapshot resumedSnapshot = ctx.LastCommittedSnapshot!;
        ulong[] resumed = resumedSnapshot.StateNodes.Select(node => node.Pointer).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That((resumedSnapshot.BufferPin!.WriteBoundary >> NodeRecordBuffer.OffsetBits), Is.EqualTo((interleavedSnapshot.BufferPin!.WriteBoundary >> NodeRecordBuffer.OffsetBits)));
            Assert.That(resumedSnapshot.BufferPin!.WriteBoundary, Is.LessThan(interleavedSnapshot.BufferPin!.WriteBoundary));
            Assert.That(resumed, Has.Some.LessThan(interleavedSnapshot.BufferPin!.WriteBoundary), "the paused scope retains its earlier reachable writes");
            Assert.That(resumed, Does.Not.Contain(interleavedSnapshot.StateRootPointer));
            Assert.That(resumed.Length, Is.EqualTo(ctx.NodeRecordBuffer.EnumerateNodes(resumedSnapshot.StateRootPointer).Count()));
            Assert.That(interleavedSnapshot.StateNodes.Count(), Is.EqualTo(1));
        }
    }

    [Test]
    public void UnchangedStateRootDoesNotEnumerateInheritedOrRevertedWrites([Values] bool revert)
    {
        using TestContext ctx = new();
        Account original = new(1, 100);
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = ctx.Scope.StartWriteBatch(1))
            batch.Set(TestItem.AddressA, original);
        ctx.Scope.Commit(1);
        StateId initial = ctx.LastCommittedSnapshot!.To;
        if (revert)
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = ctx.Scope.StartWriteBatch(1))
                batch.Set(TestItem.AddressA, new Account(2, 200));
            using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = ctx.Scope.StartWriteBatch(1))
                batch.Set(TestItem.AddressA, original);
        }
        ctx.Scope.Commit(2);
        Snapshot snapshot = ctx.LastCommittedSnapshot!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.From, Is.EqualTo(initial));
            Assert.That(snapshot.To.StateRoot, Is.EqualTo(initial.StateRoot));
            Assert.That(snapshot.StateNodes, Is.Empty);
            Assert.That(snapshot.TryGetStateNode(TreePath.Empty, out _), Is.True);
        }
    }

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
            storageBatch.Set(slot1, new UInt256(val1, isBigEndian: true));
            storageBatch.Dispose();
        }

        scope.Commit(1);

        // Verify all committed to snapshot
        Assert.That(ctx.LastCommittedSnapshot, Is.Not.Null);
        ctx.LastCommittedSnapshot!.TryGetAccount(addr1, out Account? committedAcc1);
        Assert.That(committedAcc1!.Balance, Is.EqualTo(acc1.Balance));

        ctx.LastCommittedSnapshot!.TryGetAccount(addr2, out Account? committedAcc2);
        Assert.That(committedAcc2!.Balance, Is.EqualTo(acc2.Balance));

        ctx.LastCommittedSnapshot!.TryGetStorage((addr1, slot1), out UInt256? committedSlot);
        Assert.That(committedSlot!.Value.ToMinimalBigEndian(), Is.EqualTo(val1));
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
        UInt256 outVal = BaseFlatPersistence.DecodeSlotValue(persistedVal);
        ctx.PersistenceReader.TryGetSlot(addr, slot, ref Arg.Any<UInt256>())
            .Returns(x => { x[2] = outVal; return true; });

        // Snapshot Setup
        ctx.AddSnapshot(content => content.Storages[(addr, slot)] = BaseFlatPersistence.DecodeSlotValue(snapshotVal));
        ctx.AddSnapshot(content => content.SelfDestructedStorageAddresses[addr] = true);
        ctx.AddSnapshot(content => { });

        // Verify both are blocked
        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(addr);
        storageTree.Get(slot, out UInt256 slotRead761);
        Assert.That(slotRead761, Is.EqualTo(UInt256.Zero));
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
            content.Storages[(addr1, slot1)] = BaseFlatPersistence.DecodeSlotValue(value1);

            // Also add a storage trie node for addr1 at root path
            content.SetStorageNodeRlp(ctx.NodeRecordBuffer, addr1Hash, TreePath.Empty, FlatTestHelpers.LeafRlpA);
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
        storageTree.Get(slot1, out UInt256 slotRead806);
        Assert.That(slotRead806, Is.EqualTo(new UInt256(value1, isBigEndian: true)));
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
            storageBatch.Set(slotBefore, new UInt256(valueBefore, isBigEndian: true));
            storageBatch.Dispose();
        }
        scope.Commit(1);

        // Commit 2: Self-destruct AND set new slot in same commit
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(addr, 1);
            storageBatch.Clear();
            storageBatch.Set(slotAtSelfDestruct, new UInt256(valueAtSelfDestruct, isBigEndian: true));
            storageBatch.Dispose();
        }
        scope.Commit(2);

        // Commit 3: Set slot AFTER self-destruct
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(addr, 1);
            storageBatch.Set(slotAfter, new UInt256(valueAfter, isBigEndian: true));
            storageBatch.Dispose();
        }
        scope.Commit(3);

        // Verify storage behavior:
        // - slotBefore should be blocked by self-destruct (return zero)
        // - slotAtSelfDestruct should be found (set in same commit as self-destruct)
        // - slotAfter should be found (added after self-destruct)
        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(addr);
        storageTree.Get(slotBefore, out UInt256 slotRead865);
        Assert.That(slotRead865, Is.EqualTo(UInt256.Zero), "Slot before self-destruct should be zero");
        storageTree.Get(slotAtSelfDestruct, out UInt256 slotRead866);
        Assert.That(slotRead866, Is.EqualTo(new UInt256(valueAtSelfDestruct, isBigEndian: true)), "Slot at self-destruct should be found");
        storageTree.Get(slotAfter, out UInt256 slotRead867);
        Assert.That(slotRead867, Is.EqualTo(new UInt256(valueAfter, isBigEndian: true)), "Slot after self-destruct should be found");
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
            content.Storages[(addr, slotBefore)] = BaseFlatPersistence.DecodeSlotValue(valueBeforeSelfDestruct));

        // Read-only snapshot 1: self-destruct marker (in ReadOnlySnapshotBundle)
        ctx.AddSnapshot(content => content.SelfDestructedStorageAddresses[addr] = false);

        // Local commit 1: write storage after self-destruct
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(addr, 1);
            storageBatch.Set(slotAfter1, new UInt256(valueAfter1, isBigEndian: true));
            storageBatch.Dispose();
        }
        scope.Commit(1);

        // Local commit 2: write another storage slot after self-destruct
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(addr, 1);
            storageBatch.Set(slotAfter2, new UInt256(valueAfter2, isBigEndian: true));
            storageBatch.Dispose();
        }
        scope.Commit(2);

        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(addr);

        // Slots written after self-destruct in local snapshots should be visible
        storageTree.Get(slotAfter1, out UInt256 slotRead920);
        Assert.That(slotRead920, Is.EqualTo(new UInt256(valueAfter1, isBigEndian: true)), "Slot in local snapshot after read-only self-destruct should be visible");
        storageTree.Get(slotAfter2, out UInt256 slotRead921);
        Assert.That(slotRead921, Is.EqualTo(new UInt256(valueAfter2, isBigEndian: true)), "Slot in local snapshot after read-only self-destruct should be visible");

        // Slot from before self-destruct (in read-only snapshot) should be blocked
        storageTree.Get(slotBefore, out UInt256 slotRead924);
        Assert.That(slotRead924, Is.EqualTo(UInt256.Zero), "Slot before self-destruct should be zero");
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

    /// <summary>A job still queued when the scope is disposed must skip its walk, so the wait covers only walks in flight.</summary>
    [Test]
    public async Task Dispose_QueuedWarmup_SkipsItsWalk()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;

        // A job queued before disposal carries the sequence id it was queued under.
        int queuedSequenceId = scope.HintSequenceId;
        scope.IncrementOutstandingWarmups();
        using ManualResetEventSlim waitEntered = new(false);
        // Holds dispose before its wait, so the bundle is still live when the job runs.
        using ManualResetEventSlim jobRan = new(false);
        scope.OnWaitingForWarmups = () =>
        {
            waitEntered.Set();
            jobRan.Wait();
        };

        Task disposeTask = Task.Run(() => scope.Dispose());
        bool walked;
        try
        {
            Assert.That(waitEntered.Wait(5000), Is.True, "Dispose should enter the wait loop");
            // The warmer dequeues it only now, as it would behind a backlog.
            walked = scope.WarmUpStateTrie(TestItem.AddressA, queuedSequenceId);
        }
        finally
        {
            jobRan.Set();
        }
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(walked, Is.False, "a job dequeued after disposal began must not walk the trie");
    }

    /// <summary>Dispose must return when the last warmup completes, not when its timeout expires.</summary>
    [Test]
    public async Task Dispose_ReturnsWhenTheLastWarmupCompletes()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;
        scope.IncrementOutstandingWarmups();
        long completed = 0;
        // The last job completes after dispose has re-read the count and just before it blocks.
        scope.OnBlockingForWarmups = () =>
        {
            scope.DecrementOutstandingWarmups();
            completed = Stopwatch.GetTimestamp();
        };

        // Measured on the disposing thread, so scheduling of this test's continuations is not counted.
        TimeSpan blocked = await Task.Run(() =>
        {
            scope.Dispose();
            return Stopwatch.GetElapsedTime(completed);
        }).WaitAsync(TimeSpan.FromSeconds(5));

        // Without the wake, dispose returns only when its one-second timeout expires.
        Assert.That(blocked, Is.LessThan(TimeSpan.FromMilliseconds(900)),
            "the completing job must wake dispose rather than leave it to time out");
    }

    /// <summary>A wake from a job that reached zero before more were queued must not end the wait while one is still in flight.</summary>
    [Test]
    public async Task Dispose_StaleWake_KeepsWaitingForTheJobInFlight()
    {
        using TestContext ctx = new();
        FlatWorldStateScope scope = ctx.Scope;
        scope.IncrementOutstandingWarmups();
        int blocks = 0;
        scope.OnBlockingForWarmups = () =>
        {
            if (Interlocked.Increment(ref blocks) == 1)
            {
                // One job completes to zero and sets the event, then another is counted: the wake is stale.
                scope.DecrementOutstandingWarmups();
                scope.IncrementOutstandingWarmups();
            }
            else
            {
                // Blocking again proves the stale wake did not end the wait; the job in flight completes now.
                scope.DecrementOutstandingWarmups();
            }
        };

        await Task.Run(() => scope.Dispose()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(Volatile.Read(ref blocks), Is.EqualTo(2), "dispose must wait again after a stale wake instead of returning");
    }

    public enum WarmupHint
    {
        Account,
        BalAccount,
        StorageSlot,
        HintSlot,
        BalSlot,
    }

    /// <summary>
    /// A job may complete before its push returns. Counting it only afterwards drives the count below zero, and then a
    /// later increment can bring it to zero with no completion to wake a disposing scope, which waits out its timeout.
    /// </summary>
    [Test]
    public async Task Warmup_CompletingDuringItsPush_NeverDrivesTheCountBelowZero([Values] WarmupHint hint)
    {
        InlineTrieWarmer warmer = new();
        using TestContext ctx = new(trieWarmer: warmer);
        // The slot hints warm only an account whose storage trie is not empty.
        ctx.PersistenceReader.GetAccount(TestItem.AddressA).Returns(new Account(1, 1, TestItem.KeccakA, Keccak.OfAnEmptyString));
        FlatWorldStateScope scope = ctx.Scope;
        warmer.Scope = scope;

        switch (hint)
        {
            case WarmupHint.Account:
                scope.HintWarmAccount(TestItem.AddressA);
                break;
            case WarmupHint.BalAccount:
                await scope.HintBal(CreateBal(WrittenAccount(TestItem.AddressA)));
                break;
            case WarmupHint.StorageSlot:
                scope.CreateStorageTree(TestItem.AddressA).HintSet((UInt256)1);
                break;
            case WarmupHint.HintSlot:
                scope.HintWarmSlot(TestItem.AddressA, (UInt256)1);
                break;
            case WarmupHint.BalSlot:
                await scope.HintBal(CreateBal(WrittenAccount(TestItem.AddressA, BalWriteKind.Storage)));
                break;
        }

        int expectedJobs = hint is WarmupHint.Account or WarmupHint.BalAccount ? warmer.AddressJobs : warmer.SlotJobs;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(expectedJobs, Is.EqualTo(1), "precondition: the hint must have run its job inside its push");
            Assert.That(warmer.LowestCountAfterCompletion, Is.GreaterThanOrEqualTo(0));
            Assert.That(scope.OutstandingWarmups, Is.Zero);
        }
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
        ReadOnlySnapshotBundle readOnlyBundle = new(new SnapshotPooledList(0), reader, recordDetailedMetrics: false, PersistedSnapshotStack.Empty(), bufferPin: null);
        FlatDbConfig config = new();
        ResourcePool resourcePool = new(config);
        using INodeRecordBuffer buffer = FlatTestHelpers.NewTestBuffer();
        SnapshotBundle bundle = new(readOnlyBundle, buffer, resourcePool, ResourcePool.Usage.MainBlockProcessing);
        await using TrieWarmer warmer = new(LimboLogs.Instance, config);

        using FlatWorldStateScope scope = new(
            new StateId(0, TestItem.KeccakA),
            bundle,
            new TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb(new TestMemDb()),
            Substitute.For<IFlatCommitTarget>(),
            config,
            warmer,
            LimboLogs.Instance);

        // Queues a state-trie warmup job whose traversal blocks inside the persistence reader,
        // simulating the slow cold read that is in flight when a restart-replay scope is disposed.
        scope.HintWarmAccount(TestItem.AddressA);
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

        storageTree.HintSet((UInt256)1);

        Assert.That(warmer.SlotJobPushes, Is.EqualTo(1));
        Assert.That(warmer.MpmcSlotJobPushes, Is.EqualTo(slotRingAccepts ? 0 : 1));

        // The dedupe bloom is already marked, so a repeated hint for the same slot must not push again.
        storageTree.HintSet((UInt256)1);
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

    private static ReadOnlyBlockAccessList CreateBal(params ReadOnlyAccountChanges[] accounts)
    {
        Array.Sort(accounts, static (a, b) => a.Address.Bytes.SequenceCompareTo(b.Address.Bytes));
        return new ReadOnlyBlockAccessList(accounts, accounts.Length);
    }

    private static ReadOnlyAccountChanges ReadOnlyAccount(Address address) =>
        new(address, storageChanges: [], storageReads: [(UInt256)1], balanceChanges: [], nonceChanges: [], codeChanges: []);

    public enum BalWriteKind
    {
        Balance,
        Nonce,
        Code,
        Storage,
    }

    private static ReadOnlyAccountChanges WrittenAccount(Address address, BalWriteKind kind = BalWriteKind.Balance) => kind switch
    {
        BalWriteKind.Balance => new(address, storageChanges: [], storageReads: [], balanceChanges: [new BalanceChange(0, UInt256.One)], nonceChanges: [], codeChanges: []),
        BalWriteKind.Nonce => new(address, storageChanges: [], storageReads: [], balanceChanges: [], nonceChanges: [new NonceChange(0, 1)], codeChanges: []),
        BalWriteKind.Code => new(address, storageChanges: [], storageReads: [], balanceChanges: [], nonceChanges: [], codeChanges: [new CodeChange(0, [0x00])]),
        BalWriteKind.Storage => new(address, storageChanges: [new ReadOnlySlotChanges((UInt256)1, [new StorageChange(0, default)])], storageReads: [], balanceChanges: [], nonceChanges: [], codeChanges: []),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>A persistence reader that holds nothing and records the keys it is asked to prefetch.</summary>
    private sealed class PrefetchRecordingReader : IPersistence.IPersistenceReader, IPersistence.IPrefetchingReader
    {
        private readonly IPersistence.IPersistenceReader _empty = Substitute.For<IPersistence.IPersistenceReader>();

        public List<Address> Accounts { get; } = [];
        public List<StorageCell> Slots { get; } = [];
        public bool SupportsPrefetch { get; init; } = true;

        public void Prefetch(Span<Address> accounts, Span<StorageCell> slots, CancellationToken cancellationToken)
        {
            lock (Accounts)
            {
                Accounts.AddRange(accounts);
                Slots.AddRange(slots);
            }
        }

        public Account? GetAccount(Address address) => _empty.GetAccount(address);
        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue) => _empty.TryGetSlot(address, slot, ref outValue);
        public StateId CurrentState => _empty.CurrentState;
        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => _empty.TryLoadStateRlp(path, flags);
        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => _empty.TryLoadStorageRlp(address, path, flags);
        public bool TryLoadStateRlp<TArg>(in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct =>
            _empty.TryLoadStateRlp(path, flags, action, arg);
        public bool TryLoadStorageRlp<TArg>(Hash256 address, in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct =>
            _empty.TryLoadStorageRlp(address, path, flags, action, arg);
        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => _empty.GetAccountRaw(addrHash);
        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => _empty.TryGetStorageRaw(addrHash, slotHash, ref value);
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => _empty.CreateAccountIterator(startKey, endKey);
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) =>
            _empty.CreateStorageIterator(accountKey, startSlotKey, endSlotKey);
        public bool IsPreimageMode => false;
        public void Dispose() { }
    }

    private static TestContext CreateContextWithRecordingWarmer(out RecordingTrieWarmer warmer)
    {
        warmer = new RecordingTrieWarmer(acceptSlotJob: true, acceptMpmcSlotJob: true);
        return new TestContext(trieWarmer: warmer);
    }

    [Test]
    public async Task HintBal_SkipsAddressWarmup_ForReadOnlyBalAccounts()
    {
        using TestContext ctx = CreateContextWithRecordingWarmer(out RecordingTrieWarmer warmer);
        FlatWorldStateScope scope = ctx.Scope;

        await scope.HintBal(CreateBal(ReadOnlyAccount(TestItem.AddressA), WrittenAccount(TestItem.AddressB)));

        Assert.That(warmer.AddressJobPushes, Is.EquivalentTo(new[] { TestItem.AddressB }));
    }

    // Storage-only changes must still warm: the storage-root change rewrites the account leaf.
    [Test]
    public async Task HintBal_WarmsAddress_ForEachWriteKind([Values(BalWriteKind.Balance, BalWriteKind.Nonce, BalWriteKind.Code, BalWriteKind.Storage)] BalWriteKind kind)
    {
        using TestContext ctx = CreateContextWithRecordingWarmer(out RecordingTrieWarmer warmer);
        FlatWorldStateScope scope = ctx.Scope;

        await scope.HintBal(CreateBal(WrittenAccount(TestItem.AddressA, kind)));

        Assert.That(warmer.AddressJobPushes, Is.EquivalentTo(new[] { TestItem.AddressA }));
    }

    [Test]
    public async Task HintBal_WithSink_StillReadsReadOnlyAccounts()
    {
        using TestContext ctx = CreateContextWithRecordingWarmer(out RecordingTrieWarmer warmer);
        FlatWorldStateScope scope = ctx.Scope;
        RecordingBalReaderSink sink = new();

        await scope.HintBal(CreateBal(ReadOnlyAccount(TestItem.AddressA), WrittenAccount(TestItem.AddressB)), sink);

        Assert.That(sink.AccountReads, Is.EquivalentTo(new[] { TestItem.AddressA, TestItem.AddressB }));
        Assert.That(warmer.AddressJobPushes, Is.EquivalentTo(new[] { TestItem.AddressB }));
    }

    [Test]
    public async Task HintBal_EmptyBal_ResetsPreviousWriteSet()
    {
        using TestContext ctx = CreateContextWithRecordingWarmer(out RecordingTrieWarmer warmer);
        FlatWorldStateScope scope = ctx.Scope;

        await scope.HintBal(CreateBal(ReadOnlyAccount(TestItem.AddressA)));
        await scope.HintBal(CreateBal());

        scope.HintWarmAccount(TestItem.AddressA);

        Assert.That(warmer.AddressJobPushes, Is.EquivalentTo(new[] { TestItem.AddressA }));
    }

    [Test]
    public async Task HintWarmAccount_AfterHintBal_SkipsReadOnlyAndUnlistedAccounts()
    {
        using TestContext ctx = CreateContextWithRecordingWarmer(out RecordingTrieWarmer warmer);
        FlatWorldStateScope scope = ctx.Scope;

        await scope.HintBal(CreateBal(ReadOnlyAccount(TestItem.AddressA), WrittenAccount(TestItem.AddressB)));

        scope.HintWarmAccount(TestItem.AddressA); // read-only in BAL
        scope.HintWarmAccount(TestItem.AddressC); // not in BAL
        scope.HintWarmAccount(TestItem.AddressB); // written, but already pushed by HintBal (bloom dedupe)

        Assert.That(warmer.AddressJobPushes, Is.EquivalentTo(new[] { TestItem.AddressB }));
    }

    [Test]
    public async Task StartWriteBatch_KeepsBalWarmupFilter()
    {
        using TestContext ctx = CreateContextWithRecordingWarmer(out RecordingTrieWarmer warmer);
        FlatWorldStateScope scope = ctx.Scope;

        await scope.HintBal(CreateBal(ReadOnlyAccount(TestItem.AddressA)));
        scope.StartWriteBatch(0).Dispose();

        scope.HintWarmAccount(TestItem.AddressA);

        Assert.That(warmer.AddressJobPushes, Is.Empty);
    }

    // The BAL prefetch has the warmup's guard: a hint that is paused when its task starts sends nothing, so
    // CancelHintBal never waits for a key collection and submission the block no longer needs.
    [TestCase(false, TestName = "HintBal_CurrentHint_PrefetchesItsKeys")]
    [TestCase(true, TestName = "HintBal_PausedHint_PrefetchesNothing")]
    public async Task HintBal_PrefetchesOnlyWhileTheHintIsCurrent(bool paused)
    {
        PrefetchRecordingReader reader = new();
        using TestContext ctx = new(persistenceReader: reader);
        FlatWorldStateScope scope = ctx.Scope;
        scope._pausePrewarmer = paused;

        await scope.HintBal(CreateBal(WrittenAccount(TestItem.AddressA, BalWriteKind.Storage), ReadOnlyAccount(TestItem.AddressB)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.Accounts, Is.EquivalentTo(paused ? Array.Empty<Address>() : new[] { TestItem.AddressA, TestItem.AddressB }));
            Assert.That(reader.Slots, Is.EquivalentTo(paused ? Array.Empty<StorageCell>() : new[] { new StorageCell(TestItem.AddressA, 1), new StorageCell(TestItem.AddressB, 1) }));
        }
    }

    [Test]
    public async Task HintBal_StoreThatCannotPrefetch_CollectsNoKeys()
    {
        PrefetchRecordingReader reader = new() { SupportsPrefetch = false };
        using TestContext ctx = new(persistenceReader: reader);
        FlatWorldStateScope scope = ctx.Scope;

        await scope.HintBal(CreateBal(WrittenAccount(TestItem.AddressA, BalWriteKind.Storage), ReadOnlyAccount(TestItem.AddressB)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.SupportsPrefetch, Is.False, "the scope reports the store's prefetch support");
            Assert.That(reader.Accounts, Is.Empty);
            Assert.That(reader.Slots, Is.Empty);
        }
    }

    [Test]
    public void CollectBalKeys_StopsWhenCancelled([Values] bool cancelled)
    {
        const int SlotCount = 3000;
        UInt256[] reads = new UInt256[SlotCount];
        for (int i = 0; i < reads.Length; i++) reads[i] = (UInt256)i;
        using ArrayPoolList<ReadOnlyAccountChanges> accountChanges = new(2)
        {
            ReadOnlyAccount(TestItem.AddressA),
            new ReadOnlyAccountChanges(TestItem.AddressB, [], reads, [], [], [])
        };
        Address[] accounts = new Address[accountChanges.Count];
        StorageCell[] slots = new StorageCell[SlotCount + 1];

        bool completed = FlatWorldStateScope.CollectBalKeys(accountChanges, accounts, slots, new CancellationToken(cancelled), out int accountCount, out int slotCount);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed, Is.EqualTo(!cancelled));
            Assert.That(accountCount, Is.EqualTo(cancelled ? 0 : 2));
            Assert.That(slotCount, Is.EqualTo(cancelled ? 0 : SlotCount + 1));
        }
    }

    [Test]
    public async Task HintBal_SecondBal_ReplacesPreviousWriteSet()
    {
        using TestContext ctx = CreateContextWithRecordingWarmer(out RecordingTrieWarmer warmer);
        FlatWorldStateScope scope = ctx.Scope;

        // paused so the first BAL's warmup doesn't bloom-mark AddressA and mask the assert
        scope._pausePrewarmer = true;
        await scope.HintBal(CreateBal(WrittenAccount(TestItem.AddressA)));
        scope._pausePrewarmer = false;

        await scope.HintBal(CreateBal(ReadOnlyAccount(TestItem.AddressB)));

        scope.HintWarmAccount(TestItem.AddressA);

        Assert.That(warmer.AddressJobPushes, Is.Empty);
    }

    [Test]
    public void HintWarmAccount_WarmsEverything_WithoutBalHint()
    {
        using TestContext ctx = CreateContextWithRecordingWarmer(out RecordingTrieWarmer warmer);
        FlatWorldStateScope scope = ctx.Scope;

        scope.HintWarmAccount(TestItem.AddressA);

        Assert.That(warmer.AddressJobPushes, Is.EquivalentTo(new[] { TestItem.AddressA }));
    }

    [TestCase(true, TestName = "Hints_WithActiveWarmer_MarkTheDedupeBloomAndReachTheWarmer")]
    [TestCase(false, TestName = "Hints_WithNoopWarmer_LeaveTheDedupeBloomUntouched")]
    public void Hints_FollowWhetherTheWarmerIsActive(bool activeWarmer)
    {
        // Both push kinds are rejected, so no warm-up stays outstanding past the asserts.
        RecordingTrieWarmer recording = new(acceptSlotJob: false, acceptMpmcSlotJob: false);
        using TestContext ctx = new(trieWarmer: activeWarmer ? recording : new NoopTrieWarmer());
        FlatWorldStateScope scope = ctx.Scope;

        scope.HintWarmAccount(TestItem.AddressA);
        scope.HintWarmSlot(TestItem.AddressC, (UInt256)2);
        scope.CreateStorageTree(TestItem.AddressB).HintSet((UInt256)1);

        SnapshotBundle bundle = ctx.SnapshotBundle;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.ShouldQueuePrewarm(TestItem.AddressA), Is.EqualTo(!activeWarmer), "an account hint marks the bloom only for an active warmer");
            Assert.That(bundle.ShouldQueuePrewarm(TestItem.AddressC, (UInt256)2), Is.EqualTo(!activeWarmer), "a slot hint marks the bloom only for an active warmer");
            Assert.That(bundle.ShouldQueuePrewarm(TestItem.AddressB, (UInt256)1), Is.EqualTo(!activeWarmer), "a storage HintSet marks the bloom only for an active warmer");
            Assert.That(recording.AddressJobPushes, Is.EqualTo(activeWarmer ? new[] { TestItem.AddressA } : Array.Empty<Address>()), "the account hint reaches an active warmer");
            Assert.That(recording.SlotJobPushes, Is.EqualTo(activeWarmer ? 1 : 0), "the storage HintSet reaches an active warmer");
        }
    }

    private sealed class RecordingBalReaderSink : IWorldStateScopeProvider.IAsyncBalReaderSink
    {
        private readonly Lock _lock = new();
        private readonly List<Address> _accountReads = [];

        public Address[] AccountReads
        {
            get
            {
                lock (_lock) return _accountReads.ToArray();
            }
        }

        public void OnAccountRead(Address address, Account? account)
        {
            lock (_lock) _accountReads.Add(address);
        }

        public void OnStorageRead(in StorageCell storageCell, in UInt256 value) { }

        public bool StillNeeded(Address address, out Account? account)
        {
            account = null;
            return true;
        }

        public bool StillNeeded(in StorageCell storageCell) => true;
    }

    private sealed class RecordingTrieWarmer(bool acceptSlotJob, bool acceptMpmcSlotJob) : ITrieWarmer
    {
        private readonly Lock _lock = new();
        private readonly List<Address> _addressJobPushes = [];

        public int SlotJobPushes { get; private set; }
        public int MpmcSlotJobPushes { get; private set; }

        public Address[] AddressJobPushes
        {
            get
            {
                lock (_lock) return _addressJobPushes.ToArray();
            }
        }

        public bool PushSlotJob(ITrieWarmer.IStorageWarmer storageTree, in UInt256 index, int sequenceId, bool isDelete)
        {
            SlotJobPushes++;
            return acceptSlotJob;
        }

        public bool PushSlotJobMpmc(ITrieWarmer.IStorageWarmer storageTree, in UInt256 index, int sequenceId, bool isDelete)
        {
            MpmcSlotJobPushes++;
            return acceptMpmcSlotJob;
        }

        public bool PushAddressJob(ITrieWarmer.IAddressWarmer scope, Address? path, int sequenceId)
        {
            lock (_lock)
            {
                if (path is not null) _addressJobPushes.Add(path);
            }
            return false;
        }

        public void OnEnterScope() { }

        public void OnExitScope() { }
    }

    /// <summary>Runs each job inside its push, as a worker that dequeues it at once would, and records the count after.</summary>
    private sealed class InlineTrieWarmer : ITrieWarmer
    {
        public FlatWorldStateScope? Scope { get; set; }
        public int AddressJobs { get; private set; }
        public int SlotJobs { get; private set; }
        public int LowestCountAfterCompletion { get; private set; } = int.MaxValue;

        public bool PushSlotJob(ITrieWarmer.IStorageWarmer storageTree, in UInt256 index, int sequenceId, bool isDelete)
            => PushSlotJobMpmc(storageTree, index, sequenceId, isDelete);

        public bool PushSlotJobMpmc(ITrieWarmer.IStorageWarmer storageTree, in UInt256 index, int sequenceId, bool isDelete)
        {
            // The mocked persistence holds no trie nodes; the real warmer drops the same exception.
            try { storageTree.WarmUpStorageTrie(index, sequenceId, isDelete); }
            catch (TrieNodeException) { }
            SlotJobs++;
            return Record();
        }

        public bool PushAddressJob(ITrieWarmer.IAddressWarmer scope, Address? path, int sequenceId)
        {
            try { scope.WarmUpStateTrie(path!, sequenceId); }
            catch (TrieNodeException) { }
            AddressJobs++;
            return Record();
        }

        private bool Record()
        {
            LowestCountAfterCompletion = Math.Min(LowestCountAfterCompletion, Scope!.OutstandingWarmups);
            return true;
        }

        public void OnEnterScope() { }

        public void OnExitScope() { }
    }

    /// <summary>Holds one trie node, at <paramref name="path"/> of the state trie and of every storage trie.</summary>
    private sealed class SinglePersistedNodeReader(TreePath path, byte[] rlp) : IPersistence.IPersistenceReader
    {
        public int Loads { get; private set; }

        public byte[]? TryLoadStateRlp(in TreePath nodePath, ReadFlags flags)
        {
            Loads++;
            return nodePath == path ? rlp : null;
        }

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath nodePath, ReadFlags flags) => TryLoadStateRlp(nodePath, flags);

        public bool TryLoadStateRlp<TArg>(in TreePath nodePath, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
        {
            Loads++;
            if (nodePath != path) return false;
            action(rlp, arg);
            return true;
        }

        public bool TryLoadStorageRlp<TArg>(Hash256 address, in TreePath nodePath, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct =>
            TryLoadStateRlp(nodePath, flags, action, arg);

        public Account? GetAccount(Address address) => null;
        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue) => false;
        public StateId CurrentState => new(0, Keccak.EmptyTreeHash);
        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => null;
        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => false;
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => throw new NotSupportedException();
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => throw new NotSupportedException();
        public bool IsPreimageMode => false;
        public void Dispose() { }
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

        public bool TryLoadStateRlp<TArg>(in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
        {
            TryLoadStateRlp(path, flags);
            return false;
        }

        public bool TryLoadStorageRlp<TArg>(Hash256 address, in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct => false;

        public void Dispose()
        {
            if (Volatile.Read(ref _activeReads) != 0) _disposedDuringActiveRead = true;
            _isDisposed = true;
            ReadEntered.Dispose();
            ResumeReads.Dispose();
        }

        public Account? GetAccount(Address address) => null;
        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue) => false;
        public StateId CurrentState => new(0, Keccak.EmptyTreeHash);
        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => null;
        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => null;
        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => false;
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => throw new NotSupportedException();
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => throw new NotSupportedException();
        public bool IsPreimageMode => false;
    }

    public enum ScopeProviderKind
    {
        Global,
        Resettable,
        ReadOnlyQuery,
    }
}
