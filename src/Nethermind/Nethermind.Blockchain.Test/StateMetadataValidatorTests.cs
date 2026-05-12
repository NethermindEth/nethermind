// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

public class StateMetadataValidatorTests
{
    private const long FloorBlock = 100;

    [Test]
    public void Clears_OldestStateBlock_when_state_root_missing()
    {
        Fixture f = new();
        f.WorldStateManager.OldestStateBlock.Returns(FloorBlock);
        f.WithHeaderAt(FloorBlock, stateAvailable: false);

        StateMetadataValidator.DiscardStaleFloors(f.WorldStateManager, f.BlockTree, LimboLogs.Instance);

        f.WorldStateManager.Received().OldestStateBlock = null;
    }

    [Test]
    public void Keeps_OldestStateBlock_when_state_root_present()
    {
        Fixture f = new();
        f.WorldStateManager.OldestStateBlock.Returns(FloorBlock);
        f.WithHeaderAt(FloorBlock, stateAvailable: true);

        StateMetadataValidator.DiscardStaleFloors(f.WorldStateManager, f.BlockTree, LimboLogs.Instance);

        f.WorldStateManager.DidNotReceive().OldestStateBlock = Arg.Any<long?>();
    }

    [Test]
    public void Keeps_OldestStateBlock_when_header_unknown()
    {
        Fixture f = new();
        f.WorldStateManager.OldestStateBlock.Returns(FloorBlock);
        // No header recorded — FindHeader returns null. Should not call HasStateForBlock.

        StateMetadataValidator.DiscardStaleFloors(f.WorldStateManager, f.BlockTree, LimboLogs.Instance);

        f.WorldStateManager.DidNotReceive().OldestStateBlock = Arg.Any<long?>();
        f.StateReader.DidNotReceive().HasStateForBlock(Arg.Any<BlockHeader>());
    }

    [Test]
    public void Does_nothing_when_OldestStateBlock_is_null()
    {
        Fixture f = new();
        f.WorldStateManager.OldestStateBlock.Returns((long?)null);

        StateMetadataValidator.DiscardStaleFloors(f.WorldStateManager, f.BlockTree, LimboLogs.Instance);

        f.WorldStateManager.DidNotReceive().OldestStateBlock = Arg.Any<long?>();
        f.BlockTree.DidNotReceive().FindHeader(Arg.Any<long>(), Arg.Any<BlockTreeLookupOptions>());
    }

    [Test]
    public void Clears_BestPersistedState_when_state_root_missing()
    {
        Fixture f = new();
        f.BlockTree.BestPersistedState.Returns(FloorBlock);
        f.WithHeaderAt(FloorBlock, stateAvailable: false);

        StateMetadataValidator.DiscardStaleFloors(f.WorldStateManager, f.BlockTree, LimboLogs.Instance);

        f.BlockTree.Received().BestPersistedState = null;
    }

    [Test]
    public void Keeps_BestPersistedState_when_state_root_present()
    {
        Fixture f = new();
        f.BlockTree.BestPersistedState.Returns(FloorBlock);
        f.WithHeaderAt(FloorBlock, stateAvailable: true);

        StateMetadataValidator.DiscardStaleFloors(f.WorldStateManager, f.BlockTree, LimboLogs.Instance);

        f.BlockTree.DidNotReceive().BestPersistedState = Arg.Any<long?>();
    }

    [Test]
    public void Both_markers_independent()
    {
        Fixture f = new();
        f.WorldStateManager.OldestStateBlock.Returns(50L);
        f.BlockTree.BestPersistedState.Returns(200L);
        f.WithHeaderAt(50, stateAvailable: false);  // floor stale
        f.WithHeaderAt(200, stateAvailable: true);  // persisted ok

        StateMetadataValidator.DiscardStaleFloors(f.WorldStateManager, f.BlockTree, LimboLogs.Instance);

        f.WorldStateManager.Received().OldestStateBlock = null;
        f.BlockTree.DidNotReceive().BestPersistedState = Arg.Any<long?>();
    }

    private sealed class Fixture
    {
        public IWorldStateManager WorldStateManager { get; } = Substitute.For<IWorldStateManager>();
        public IBlockTree BlockTree { get; } = Substitute.For<IBlockTree>();
        public IStateReader StateReader { get; } = Substitute.For<IStateReader>();

        public Fixture() => WorldStateManager.GlobalStateReader.Returns(StateReader);

        public void WithHeaderAt(long blockNumber, bool stateAvailable)
        {
            BlockHeader header = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;
            BlockTree.FindHeader(blockNumber, Arg.Any<BlockTreeLookupOptions>()).Returns(header);
            StateReader.HasStateForBlock(header).Returns(stateAvailable);
        }
    }
}
