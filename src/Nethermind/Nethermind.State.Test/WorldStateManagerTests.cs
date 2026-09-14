// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using System;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Int256;

namespace Nethermind.Store.Test;

public class WorldStateManagerTests
{
    private static (IWorldStateScopeProvider worldState, IPruningTrieStore trieStore, WorldStateManager manager, StateBoundaryStore boundary) CreateWorldStateManager(IStateHeaderProvider stateHeaderProvider)
    {
        IWorldStateScopeProvider worldState = Substitute.For<IWorldStateScopeProvider>();
        IPruningTrieStore trieStore = Substitute.For<IPruningTrieStore>();
        IReadOnlyTrieStore readOnlyTrieStore = Substitute.For<IReadOnlyTrieStore>();
        readOnlyTrieStore.HasRoot(Arg.Any<Hash256>()).Returns(true);
        trieStore.AsReadOnly().Returns(readOnlyTrieStore);
        IDbProvider dbProvider = TestMemDbProvider.Init();
        StateBoundaryStore boundary = new(dbProvider.StateDb, dbProvider.BlockInfosDb, retentionWindowBlocks: null);
        WorldStateManager manager = new(worldState, trieStore, dbProvider, boundary, stateHeaderProvider ?? TestStateHeaderProvider.Unavailable, LimboLogs.Instance);
        return (worldState, trieStore, manager, boundary);
    }

    [Test]
    public void ShouldProxyGlobalWorldState()
    {
        (IWorldStateScopeProvider worldState, _, WorldStateManager manager, _) = CreateWorldStateManager(TestStateHeaderProvider.Unavailable);
        Assert.That(manager.GlobalWorldState, Is.EqualTo(worldState));
    }

    [Test]
    public void CreatedWorldStateScopesUseTargetParentLookup()
    {
        BlockHeader parent = Build.A.BlockHeader.WithStateRoot(TestItem.KeccakA).WithNumber(1).TestObject;
        BlockHeader target = Build.A.BlockHeader.WithParent(parent).WithTimestamp(12345).TestObject;
        (_, _, WorldStateManager manager, _) = CreateWorldStateManager(new TestStateHeaderProvider { Parent = parent });

        IWorldStateScopeProvider resettable = manager.CreateResettableWorldState();
        Assert.That(resettable.HasStateForTarget(target), Is.True);
        Assert.That(resettable.TryBeginScope(target, new LocalMetrics(), out IWorldStateScopeProvider.IScope resettableScope), Is.True);
        resettableScope!.Dispose();

        using IOverridableWorldScope overridable = manager.CreateOverridableWorldScope();
        Assert.That(overridable.WorldState.HasStateForTarget(target), Is.True);
        Assert.That(overridable.WorldState.TryBeginScope(target, new LocalMetrics(), out IWorldStateScopeProvider.IScope overridableScope), Is.True);
        overridableScope!.Dispose();
    }

    [Test]
    public void OverridableScopeResolvesTargetsFromInMemoryHeadersItCommitted()
    {
        WorldStateManager manager = TestWorldStateFactory.CreateWorldStateManagerForTest(TestMemDbProvider.Init(), LimboLogs.Instance);
        using IOverridableWorldScope overridable = manager.CreateOverridableWorldScope();
        IWorldStateScopeProvider worldState = overridable.WorldState;

        // Mirrors OverridableEnv.BuildAndOverride: overrides are committed into the in-memory base header itself.
        BlockHeader overriddenBase = Build.A.BlockHeader.WithNumber(1).WithStateRoot(Keccak.EmptyTreeHash).TestObject;
        overriddenBase.StateRoot = CommitAccount(worldState.BeginScope(overriddenBase, new LocalMetrics()), overriddenBase.Number, TestItem.AddressA);
        BlockHeader child = Build.A.BlockHeader.WithParent(overriddenBase).TestObject;

        Assert.That(worldState.HasStateForTarget(child), Is.True);
        Assert.That(worldState.TryBeginScope(child, new LocalMetrics(), out IWorldStateScopeProvider.IScope childScope), Is.True);
        Assert.That(childScope!.Get(TestItem.AddressA), Is.Not.Null);
        child.StateRoot = CommitAccount(childScope, child.Number, TestItem.AddressB);
        BlockHeader grandchild = Build.A.BlockHeader.WithParent(child).TestObject;

        Assert.That(worldState.HasStateForTarget(grandchild), Is.True);
        Assert.That(worldState.TryBeginScope(grandchild, new LocalMetrics(), out IWorldStateScopeProvider.IScope grandchildScope), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(grandchildScope!.Get(TestItem.AddressA), Is.Not.Null);
            Assert.That(grandchildScope.Get(TestItem.AddressB), Is.Not.Null);
        }
        grandchildScope.Dispose();

        // Every opened header stays known, not only the latest: a sibling of child still resolves overriddenBase.
        Assert.That(worldState.HasStateForTarget(Build.A.BlockHeader.WithParent(overriddenBase).WithTimestamp(7).TestObject), Is.True);

        overridable.ResetOverrides();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(worldState.HasStateForTarget(grandchild), Is.False);
            Assert.That(worldState.HasStateForTarget(child), Is.False);
        }
    }

    private static Hash256 CommitAccount(IWorldStateScopeProvider.IScope scope, ulong blockNumber, Address address)
    {
        using (scope)
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(address, new Account(1, 100));
            }
            scope.Commit(blockNumber);
            return scope.RootHash;
        }
    }

    [Test]
    public void ShouldPersistBestPersistedStateOnReorgBoundary()
    {
        IDbProvider dbProvider = TestMemDbProvider.Init();
        StateBoundaryStore boundary = new(dbProvider.StateDb, dbProvider.BlockInfosDb, retentionWindowBlocks: null);
        IPruningTrieStore trieStore = Substitute.For<IPruningTrieStore>();
        _ = new WorldStateManager(Substitute.For<IWorldStateScopeProvider>(), trieStore, dbProvider, boundary, TestStateHeaderProvider.Unavailable, LimboLogs.Instance);

        trieStore.ReorgBoundaryReached += Raise.EventWith<ReorgBoundaryReached>(new ReorgBoundaryReached(1));

        Assert.That(boundary.BestPersistedState, Is.EqualTo(1UL));
        // A fresh store over the same BlockInfos DB proves the value is durable, not just cached.
        Assert.That(new StateBoundaryStore(dbProvider.StateDb, dbProvider.BlockInfosDb, retentionWindowBlocks: null).BestPersistedState, Is.EqualTo(1UL));
    }

    [TestCase(INodeStorage.KeyScheme.Hash, true)]
    [TestCase(INodeStorage.KeyScheme.HalfPath, false)]
    public void ShouldNotSupportHashLookupOnHalfpath(INodeStorage.KeyScheme keyScheme, bool hashSupported)
    {
        (_, IPruningTrieStore trieStore, WorldStateManager manager, _) = CreateWorldStateManager(TestStateHeaderProvider.Unavailable);
        IReadOnlyTrieStore readOnlyTrieStore = Substitute.For<IReadOnlyTrieStore>();
        trieStore.AsReadOnly().Returns(readOnlyTrieStore);
        trieStore.Scheme.Returns(keyScheme);

        if (hashSupported)
        {
            Assert.That(manager.HashServer, Is.Not.Null);
        }
        else
        {
            Assert.That(manager.HashServer, Is.Null);
        }
    }

    [Test]
    public void ShouldAnnounceReorgOnDispose()
    {
        ulong lastBlock = 256;

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IConfigProvider configProvider = new ConfigProvider();
        // Asserts the pruning trie store's best-persisted-state reorg announcement; a patricia-only concept.
        configProvider.GetConfig<IFlatDbConfig>().Enabled = false;
        ulong reorgDepth = configProvider.GetConfig<ISyncConfig>().SnapServingMaxDepth;
        IStateHeaderProvider manualFinalizedStateProvider = Substitute.For<IStateHeaderProvider>();
        manualFinalizedStateProvider.FinalizedBlockNumber.Returns(lastBlock - reorgDepth);
        manualFinalizedStateProvider.GetFinalizedHeader(lastBlock - reorgDepth)
            .Returns(new BlockHeader(Keccak.EmptyTreeHash, Keccak.EmptyTreeHash, Address.Zero, UInt256.Zero, lastBlock - reorgDepth, 30_000_000, 0, [])
            {
                StateRoot = new Hash256("0xec6063a04d48f4b2258f36efaef76a23ba61875f5303fcf8ede2f5d160def35d")
            });

        IDb stateDb;
        IDb blockInfosDb;
        {
            using IContainer ctx = new ContainerBuilder()
                .AddModule(new TestNethermindModule(configProvider))
                .AddSingleton<IStateHeaderProvider>(manualFinalizedStateProvider)
                .AddSingleton(blockTree)
                .Build();

            stateDb = ctx.ResolveKeyed<IDb>(DbNames.State);
            blockInfosDb = ctx.ResolveKeyed<IDb>(DbNames.BlockInfos);
            MainProcessingContext mainProcessingContext = (MainProcessingContext)ctx.Resolve<IMainProcessingContext>();
            IWorldState worldState = mainProcessingContext.WorldState;
            PreBlockCaches preBlockCaches = mainProcessingContext.LifetimeScope.ResolveOptional<PreBlockCaches>();

            Hash256 stateRoot;

            using (worldState.BeginScope(IWorldState.PreGenesis))
            {
                worldState.CreateAccount(TestItem.AddressA, 1, 2);
                worldState.Commit(Cancun.Instance);
                worldState.CommitTree(1);
                stateRoot = worldState.StateRoot;
            }

            for (ulong i = 2; i <= lastBlock; i++)
            {
                BlockHeader baseBlock = Build.A.BlockHeader
                    .WithStateRoot(stateRoot)
                    .WithNumber(i - 1)
                    .TestObject;

                // No driver here to prepare the caches for each block, so reset them rather than lean on the
                // consumer scope's own staleness check.
                preBlockCaches?.ClearCaches();
                using (worldState.BeginScope(baseBlock))
                {
                    worldState.IncrementNonce(TestItem.AddressA, 1);
                    worldState.Commit(Cancun.Instance);
                    worldState.CommitTree(i);
                    stateRoot = worldState.StateRoot;
                }
            }
        }

        // The shutdown persist announces the reorg boundary; the manager must have written it
        // durably (BlockInfos DB) before the container tore down.
        Assert.That(new StateBoundaryStore(stateDb, blockInfosDb, retentionWindowBlocks: null).BestPersistedState,
            Is.EqualTo(lastBlock - reorgDepth));
    }

    [Test]
    public void CreateReadOnlyTrieStore_can_resolve_state_root([Values] bool useFlat)
    {
        IConfigProvider configProvider = new ConfigProvider();
        if (useFlat)
        {
            configProvider.GetConfig<IFlatDbConfig>().Enabled = true;
        }

        using IContainer ctx = new ContainerBuilder()
            .AddModule(new TestNethermindModule(configProvider))
            .Build();

        IWorldState worldState = ctx.Resolve<IMainProcessingContext>().WorldState;

        Hash256 stateRoot;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(TestItem.AddressA, 1, 2);
            worldState.Commit(Cancun.Instance);
            worldState.CommitTree(0);
            stateRoot = worldState.StateRoot;
        }

        BlockHeader parentHeader = Build.A.BlockHeader
            .WithStateRoot(stateRoot)
            .WithNumber(0)
            .TestObject;

        IWorldStateManager wsm = ctx.Resolve<IWorldStateManager>();
        using ITrieStore readOnlyTrieStore = wsm.CreateReadOnlyTrieStore();
        using IDisposable scope = readOnlyTrieStore.BeginScope(parentHeader);

        IScopedTrieStore scopedStore = readOnlyTrieStore.GetTrieStore(null);
        TrieNode rootNode = scopedStore.FindCachedOrUnknown(TreePath.Empty, stateRoot);

        if (rootNode.NodeType == NodeType.Unknown)
        {
            byte[] rlp = scopedStore.TryLoadRlp(TreePath.Empty, stateRoot);
            Assert.That(rlp, Is.Not.Null, "state root trie node should be resolvable from read-only trie store");
        }
        else
        {
            Assert.That(rootNode.NodeType, Is.Not.EqualTo(NodeType.Unknown), "state root should be resolvable");
        }
    }
}
