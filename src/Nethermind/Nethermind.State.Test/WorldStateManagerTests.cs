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
        ITrieStore trieStore = Substitute.For<ITrieStore>();
        IDbProvider dbProvider = TestMemDbProvider.Init();
        WorldStateProvider worldStateProvider = new(trieStore, dbProvider, LimboLogs.Instance);
        IWorldState worldState = new WorldState(trieStore, dbProvider.CodeDb, LimboLogs.Instance, null, false);
        WorldStateManager worldStateManager = new WorldStateManager(worldStateProvider, dbProvider, trieStore, LimboLogs.Instance);

        worldStateManager.GlobalWorldStateProvider.GetWorldState().StateRoot.Should().Be(worldState.StateRoot);
    }

    [Test]
    public void ShouldProxyReorgBoundaryEvent()
    {
        ITrieStore trieStore = Substitute.For<ITrieStore>();
        IDbProvider dbProvider = TestMemDbProvider.Init();
        WorldStateProvider worldStateProvider = new(trieStore, trieStore, dbProvider, LimboLogs.Instance);
        WorldStateManager worldStateManager = new WorldStateManager(worldStateProvider, dbProvider, trieStore, LimboLogs.Instance);

        bool gotEvent = false;
        worldStateManager.ReorgBoundaryReached += (sender, reached) => gotEvent = true;
        trieStore.ReorgBoundaryReached += Raise.EventWith<ReorgBoundaryReached>(new ReorgBoundaryReached(1));

        gotEvent.Should().BeTrue();
    }
}
