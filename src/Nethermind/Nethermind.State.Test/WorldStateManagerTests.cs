// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using FluentAssertions;
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
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Store.Test;

public class WorldStateManagerTests
{
    private static (IWorldStateScopeProvider worldState, IPruningTrieStore trieStore, WorldStateManager manager) CreateWorldStateManager()
    {
        IWorldStateScopeProvider worldState = Substitute.For<IWorldStateScopeProvider>();
        IPruningTrieStore trieStore = Substitute.For<IPruningTrieStore>();
        IDbProvider dbProvider = TestMemDbProvider.Init();
        WorldStateManager manager = new WorldStateManager(worldState, trieStore, dbProvider, LimboLogs.Instance);
        return (worldState, trieStore, manager);
    }

    [Test]
    public void ShouldProxyGlobalWorldState()
    {
        (IWorldStateScopeProvider worldState, _, WorldStateManager manager) = CreateWorldStateManager();
        manager.GlobalWorldState.Should().Be(worldState);
    }

    [Test]
    public void ShouldProxyReorgBoundaryEvent()
    {
        (_, IPruningTrieStore trieStore, WorldStateManager manager) = CreateWorldStateManager();

        bool gotEvent = false;
        manager.ReorgBoundaryReached += (sender, reached) => gotEvent = true;
        trieStore.ReorgBoundaryReached += Raise.EventWith<ReorgBoundaryReached>(new ReorgBoundaryReached(1));

        gotEvent.Should().BeTrue();
    }

    [TestCase(INodeStorage.KeyScheme.Hash, true)]
    [TestCase(INodeStorage.KeyScheme.HalfPath, false)]
    public void ShouldNotSupportHashLookupOnHalfpath(INodeStorage.KeyScheme keyScheme, bool hashSupported)
    {
        (_, IPruningTrieStore trieStore, WorldStateManager manager) = CreateWorldStateManager();
        IReadOnlyTrieStore readOnlyTrieStore = Substitute.For<IReadOnlyTrieStore>();
        trieStore.AsReadOnly().Returns(readOnlyTrieStore);
        trieStore.Scheme.Returns(keyScheme);

        if (hashSupported)
        {
            manager.HashServer.Should().NotBeNull();
        }
        else
        {
            manager.HashServer.Should().BeNull();
        }
    }

    [Test]
    public void ShouldAnnounceReorgOnDispose()
    {
        int lastBlock = 256;

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IConfigProvider configProvider = new ConfigProvider();
        int reorgDepth = configProvider.GetConfig<ISyncConfig>().SnapServingMaxDepth;
        IFinalizedStateProvider manualFinalizedStateProvider = Substitute.For<IFinalizedStateProvider>();
        manualFinalizedStateProvider.FinalizedBlockNumber.Returns(lastBlock - reorgDepth);
        manualFinalizedStateProvider.GetFinalizedStateRootAt(lastBlock - reorgDepth)
            .Returns(new Hash256("0xec6063a04d48f4b2258f36efaef76a23ba61875f5303fcf8ede2f5d160def35d"));

        {
            using IContainer ctx = new ContainerBuilder()
                .AddModule(new TestNethermindModule(configProvider))
                .AddSingleton<IFinalizedStateProvider>(manualFinalizedStateProvider)
                .AddSingleton(blockTree)
                .Build();

            IWorldState worldState = ctx.Resolve<IMainProcessingContext>().WorldState;

            Hash256 stateRoot;

            using (worldState.BeginScope(IWorldState.PreGenesis))
            {
                worldState.CreateAccount(TestItem.AddressA, 1, 2);
                worldState.Commit(Cancun.Instance);
                worldState.CommitTree(1);
                stateRoot = worldState.StateRoot;
            }

            for (int i = 2; i <= lastBlock; i++)
            {
                BlockHeader baseBlock = Build.A.BlockHeader
                    .WithStateRoot(stateRoot)
                    .WithNumber(i - 1)
                    .TestObject;

                using (worldState.BeginScope(baseBlock))
                {
                    worldState.IncrementNonce(TestItem.AddressA, 1);
                    worldState.Commit(Cancun.Instance);
                    worldState.CommitTree(i);
                    stateRoot = worldState.StateRoot;
                }
            }
        }

        blockTree.Received().BestPersistedState = lastBlock - reorgDepth;
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
            rlp.Should().NotBeNull("state root trie node should be resolvable from read-only trie store");
        }
        else
        {
            rootNode.NodeType.Should().NotBe(NodeType.Unknown, "state root should be resolvable");
        }
    }
}
