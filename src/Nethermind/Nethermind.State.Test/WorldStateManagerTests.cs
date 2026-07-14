// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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

namespace Nethermind.Store.Test;

public class WorldStateManagerTests
{
    private static (IWorldStateScopeProvider worldState, IPruningTrieStore trieStore, WorldStateManager manager, StateBoundaryStore boundary) CreateWorldStateManager()
    {
        IWorldStateScopeProvider worldState = Substitute.For<IWorldStateScopeProvider>();
        IPruningTrieStore trieStore = Substitute.For<IPruningTrieStore>();
        IDbProvider dbProvider = TestMemDbProvider.Init();
        StateBoundaryStore boundary = new(dbProvider.StateDb, dbProvider.BlockInfosDb, retentionWindowBlocks: null);
        WorldStateManager manager = new(worldState, trieStore, dbProvider, LimboLogs.Instance, boundary);
        return (worldState, trieStore, manager, boundary);
    }

    [Test]
    public void ShouldProxyGlobalWorldState()
    {
        (IWorldStateScopeProvider worldState, _, WorldStateManager manager, _) = CreateWorldStateManager();
        Assert.That(manager.GlobalWorldState, Is.EqualTo(worldState));
    }

    [Test]
    public void ShouldPersistBestPersistedStateOnReorgBoundary()
    {
        IDbProvider dbProvider = TestMemDbProvider.Init();
        StateBoundaryStore boundary = new(dbProvider.StateDb, dbProvider.BlockInfosDb, retentionWindowBlocks: null);
        IPruningTrieStore trieStore = Substitute.For<IPruningTrieStore>();
        _ = new WorldStateManager(Substitute.For<IWorldStateScopeProvider>(), trieStore, dbProvider, LimboLogs.Instance, boundary);

        trieStore.ReorgBoundaryReached += Raise.EventWith<ReorgBoundaryReached>(new ReorgBoundaryReached(1));

        Assert.That(boundary.BestPersistedState, Is.EqualTo(1UL));
        // A fresh store over the same BlockInfos DB proves the value is durable, not just cached.
        Assert.That(new StateBoundaryStore(dbProvider.StateDb, dbProvider.BlockInfosDb, retentionWindowBlocks: null).BestPersistedState, Is.EqualTo(1UL));
    }

    [TestCase(INodeStorage.KeyScheme.Hash, true)]
    [TestCase(INodeStorage.KeyScheme.HalfPath, false)]
    public void ShouldNotSupportHashLookupOnHalfpath(INodeStorage.KeyScheme keyScheme, bool hashSupported)
    {
        (_, IPruningTrieStore trieStore, WorldStateManager manager, _) = CreateWorldStateManager();
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
        IFinalizedStateProvider manualFinalizedStateProvider = Substitute.For<IFinalizedStateProvider>();
        manualFinalizedStateProvider.FinalizedBlockNumber.Returns(lastBlock - reorgDepth);
        manualFinalizedStateProvider.GetFinalizedStateRootAt(lastBlock - reorgDepth)
            .Returns(new Hash256("0xec6063a04d48f4b2258f36efaef76a23ba61875f5303fcf8ede2f5d160def35d"));

        IDb stateDb;
        IDb blockInfosDb;
        {
            using IContainer ctx = new ContainerBuilder()
                .AddModule(new TestNethermindModule(configProvider))
                .AddSingleton<IFinalizedStateProvider>(manualFinalizedStateProvider)
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

                // Model production: the driver clears prewarmer caches between blocks; do the same here.
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
    [TestCase(false)]
    [TestCase(true)]
    public void CreateReadOnlyTrieStore_can_resolve_state_root(bool useFlat)
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
