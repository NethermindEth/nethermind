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

    [Test]
    public void ShouldCreateTemporaryWorldState_AndCanReset()
    {
        IWorldState worldState = Substitute.For<IWorldState>();
        ITrieStore trieStore = Substitute.For<ITrieStore>();
        IDbProvider dbProvider = TestMemDbProvider.Init();
        WorldStateManager worldStateManager = new WorldStateManager(worldState, trieStore, dbProvider, LimboLogs.Instance);

        (IWorldState tempWorldState, IStateReader stateReader, Action reset) = worldStateManager.CreateResettableWorldState();

        byte[] code = new byte[] { 1 };
        Hash256 codeHash = Keccak.Compute(code);
        tempWorldState.CreateAccount(Address.Zero, 0, 0);
        tempWorldState.InsertCode(Address.Zero, code, MainnetSpecProvider.Instance.GenesisSpec);

        stateReader.GetCode(codeHash).Should().NotBeNull();
        reset();
        stateReader.GetCode(codeHash).Should().BeNull();
    }
}
