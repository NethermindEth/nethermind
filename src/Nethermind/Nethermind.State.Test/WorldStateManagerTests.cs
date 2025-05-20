// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
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
        int reorgDepth = 128; // Default reorg depth with snap serving

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IConfigProvider configProvider = new ConfigProvider();

        {
            using IContainer ctx = new ContainerBuilder()
                .AddModule(new TestNethermindModule(configProvider))
                .AddSingleton(blockTree)
                .Build();

            IWorldState worldState = ctx.Resolve<IWorldStateManager>().GlobalWorldState;

            worldState.StateRoot = Keccak.EmptyTreeHash;

            worldState.CreateAccount(TestItem.AddressA, 1, 2);
            worldState.Commit(Cancun.Instance);
            worldState.CommitTree(1);

            for (int i = 2; i <= lastBlock; i++)
            {
                worldState.IncrementNonce(TestItem.AddressA, 1);
                worldState.Commit(Cancun.Instance);
                worldState.CommitTree(i);
            }
        }

        blockTree.Received().BestPersistedState = lastBlock - reorgDepth - 1;
    }
}
