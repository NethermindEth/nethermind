// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs;
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
        WorldStateProvider worldStateProvider = new(worldState, trieStore, dbProvider, LimboLogs.Instance);
        WorldStateManager worldStateManager = new WorldStateManager(worldStateProvider, dbProvider, trieStore, LimboLogs.Instance);

        worldStateManager.WorldStateProvider.GetWorldState().Should().Be(worldState);
    }

    [Test]
    public void ShouldProxyReorgBoundaryEvent()
    {
        IWorldState worldState = Substitute.For<IWorldState>();
        ITrieStore trieStore = Substitute.For<ITrieStore>();
        IDbProvider dbProvider = TestMemDbProvider.Init();
        WorldStateProvider worldStateProvider = new(worldState, trieStore, dbProvider, LimboLogs.Instance);
        WorldStateManager worldStateManager = new WorldStateManager(worldStateProvider, dbProvider, trieStore, LimboLogs.Instance);

        bool gotEvent = false;
        worldStateManager.ReorgBoundaryReached += (sender, reached) => gotEvent = true;
        trieStore.ReorgBoundaryReached += Raise.EventWith<ReorgBoundaryReached>(new ReorgBoundaryReached(1));

        gotEvent.Should().BeTrue();
    }
}
