// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

public class StateMetadataValidatorTests
{
    public enum Marker { OldestStateBlock, BestPersistedState }
    public enum StateAt { Present, Missing, HeaderUnknown }

    [TestCase(Marker.OldestStateBlock, 100L, StateAt.Missing, /* cleared */ true)]
    [TestCase(Marker.OldestStateBlock, 100L, StateAt.Present, /* cleared */ false)]
    [TestCase(Marker.OldestStateBlock, 100L, StateAt.HeaderUnknown, /* cleared */ false)]
    [TestCase(Marker.OldestStateBlock, null, StateAt.Missing, /* cleared */ false)]
    [TestCase(Marker.BestPersistedState, 100L, StateAt.Missing, /* cleared */ true)]
    [TestCase(Marker.BestPersistedState, 100L, StateAt.Present, /* cleared */ false)]
    [TestCase(Marker.BestPersistedState, 100L, StateAt.HeaderUnknown, /* cleared */ false)]
    [TestCase(Marker.BestPersistedState, null, StateAt.Missing, /* cleared */ false)]
    public void Discard_clears_only_stale_marker(Marker marker, long? initial, StateAt stateAt, bool shouldClear)
    {
        Fixture f = new();
        f.SetMarker(marker, initial);
        if (initial is { } block && stateAt is not StateAt.HeaderUnknown)
        {
            f.WithHeaderAt(block, stateAt is StateAt.Present);
        }

        StateMetadataValidator.DiscardStaleFloors(f.Floor, f.StateReader, f.BlockTree, LimboLogs.Instance);

        f.AssertCleared(marker, shouldClear, initial);
    }

    [Test]
    public void Markers_evaluated_independently()
    {
        Fixture f = new();
        f.SetMarker(Marker.OldestStateBlock, 50L);
        f.SetMarker(Marker.BestPersistedState, 200L);
        f.WithHeaderAt(50, stateAvailable: false);  // floor stale
        f.WithHeaderAt(200, stateAvailable: true);  // persisted ok

        StateMetadataValidator.DiscardStaleFloors(f.Floor, f.StateReader, f.BlockTree, LimboLogs.Instance);

        f.AssertCleared(Marker.OldestStateBlock, shouldClear: true, initial: 50L);
        f.AssertCleared(Marker.BestPersistedState, shouldClear: false, initial: 200L);
    }

    private sealed class Fixture
    {
        public OldestStateBlockStore Floor { get; } = new(new MemDb());
        public IBlockTree BlockTree { get; } = Substitute.For<IBlockTree>();
        public IStateReader StateReader { get; } = Substitute.For<IStateReader>();

        public void WithHeaderAt(long blockNumber, bool stateAvailable)
        {
            BlockHeader header = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;
            BlockTree.FindHeader(blockNumber, Arg.Any<BlockTreeLookupOptions>()).Returns(header);
            StateReader.HasStateForBlock(header).Returns(stateAvailable);
        }

        public void SetMarker(Marker marker, long? value)
        {
            switch (marker)
            {
                case Marker.OldestStateBlock: Floor.Value = value; break;
                case Marker.BestPersistedState: BlockTree.BestPersistedState.Returns(value); break;
            }
        }

        public void AssertCleared(Marker marker, bool shouldClear, long? initial)
        {
            switch (marker)
            {
                case Marker.OldestStateBlock:
                    Assert.That(Floor.Value, Is.EqualTo(shouldClear ? null : initial));
                    break;
                case Marker.BestPersistedState:
                    if (shouldClear) BlockTree.Received().BestPersistedState = null;
                    else BlockTree.DidNotReceive().BestPersistedState = Arg.Any<long?>();
                    break;
            }
        }
    }
}
