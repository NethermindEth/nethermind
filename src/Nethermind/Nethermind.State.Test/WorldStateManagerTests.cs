// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Store.Test;

public class WorldStateManagerTests
{
    [Test]
    public void ShouldProxyGlobalWorldState()
    {
        IWorldState worldState = Substitute.For<IWorldState>();
        IPruningTrieStore trieStore = Substitute.For<IPruningTrieStore>();
        IDbProvider dbProvider = TestMemDbProvider.Init();
        WorldStateManager worldStateManager = new WorldStateManager(worldState, trieStore, dbProvider, LimboLogs.Instance);

        worldStateManager.GlobalWorldState.Should().Be(worldState);
    }

    [Test]
    public void ShouldProxyReorgBoundaryEvent()
    {
        IWorldState worldState = Substitute.For<IWorldState>();
        IPruningTrieStore trieStore = Substitute.For<IPruningTrieStore>();
        IDbProvider dbProvider = TestMemDbProvider.Init();
        WorldStateManager worldStateManager = new WorldStateManager(worldState, trieStore, dbProvider, LimboLogs.Instance);

        bool gotEvent = false;
        worldStateManager.ReorgBoundaryReached += (sender, reached) => gotEvent = true;
        trieStore.ReorgBoundaryReached += Raise.EventWith<ReorgBoundaryReached>(new ReorgBoundaryReached(1));

        gotEvent.Should().BeTrue();
    }

    [TestCase(INodeStorage.KeyScheme.Hash, true)]
    [TestCase(INodeStorage.KeyScheme.HalfPath, false)]
    public void ShouldNotSupportHashLookupOnHalfpath(INodeStorage.KeyScheme keyScheme, bool hashSupported)
    {
        IWorldState worldState = Substitute.For<IWorldState>();
        IPruningTrieStore trieStore = Substitute.For<IPruningTrieStore>();
        IReadOnlyTrieStore readOnlyTrieStore = Substitute.For<IReadOnlyTrieStore>();
        trieStore.AsReadOnly().Returns(readOnlyTrieStore);
        trieStore.Scheme.Returns(keyScheme);
        IDbProvider dbProvider = TestMemDbProvider.Init();
        WorldStateManager worldStateManager = new WorldStateManager(worldState, trieStore, dbProvider, LimboLogs.Instance);

        if (hashSupported)
        {
            worldStateManager.HashServer.Should().NotBeNull();
        }
        else
        {
            worldStateManager.HashServer.Should().BeNull();
        }
    }

    [Test]
    public void ShouldAnnounceReorgOnDispose()
    {
        int lastBlock = 256;
        int reorgDepth = 129; // Default reorg depth with snap serving, includes safety margin for HasState checks in BlockchainBridge

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IConfigProvider configProvider = new ConfigProvider();

        {
            using IContainer ctx = new ContainerBuilder()
                .AddModule(new TestNethermindModule(configProvider))
                .AddSingleton(blockTree)
                .Build();

            IWorldState worldState = ctx.Resolve<IWorldStateManager>().GlobalWorldState;

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
}
