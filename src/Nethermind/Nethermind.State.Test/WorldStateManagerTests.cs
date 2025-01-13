// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Db;
using Nethermind.Logging;
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
        ITrieStore trieStore = Substitute.For<ITrieStore>();
        IDbProvider dbProvider = TestMemDbProvider.Init();
        WorldStateManager worldStateManager = new WorldStateManager(worldState, trieStore, dbProvider, LimboLogs.Instance);

        worldStateManager.GlobalWorldState.Should().Be(worldState);
    }

    [Test]
    public void ShouldProxyReorgBoundaryEvent()
    {
        IWorldState worldState = Substitute.For<IWorldState>();
        ITrieStore trieStore = Substitute.For<ITrieStore>();
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
        ITrieStore trieStore = Substitute.For<ITrieStore>();
        IReadOnlyTrieStore readOnlyTrieStore = Substitute.For<IReadOnlyTrieStore>();
        trieStore.AsReadOnly().Returns(readOnlyTrieStore);
        readOnlyTrieStore.Scheme.Returns(keyScheme);
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
}
