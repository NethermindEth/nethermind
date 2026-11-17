// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.AuRa;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test;

public class AuRaStateSyncPivotTests
{
    [Test]
    public void Waits_until_the_trusted_anchor_is_available_and_canonical([Values] bool stored)
    {
        BlockHeader anchor = Build.A.BlockHeader.WithNumber(3).TestObject;
        BlockHeader candidate = Build.A.BlockHeader.WithParent(anchor).TestObject;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.SyncPivot.Returns((anchor.Number, anchor.Hash!));
        if (stored)
        {
            blockTree.FindHeader(anchor.Hash!, BlockTreeLookupOptions.TotalDifficultyNotNeeded, anchor.Number).Returns(anchor);
        }

        IStateSyncPivot inner = Substitute.For<IStateSyncPivot>();
        inner.GetPivotHeader().Returns(candidate);
        AuRaStateSyncPivot pivot = new(inner, blockTree, Substitute.For<ISpecProvider>(), LimboLogs.Instance);

        Assert.That(pivot.GetPivotHeader(), Is.Null);
    }
    [Test]
    public void Zero_difficulty_at_genesis_does_not_prove_a_future_merge_has_occurred()
    {
        BlockHeader genesis = Build.A.BlockHeader.WithNumber(0).WithDifficulty(0).TestObject;
        BlockHeader candidate = Build.A.BlockHeader.WithParent(genesis).TestObject;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Genesis.Returns(genesis);
        blockTree.FindHeader(genesis.Hash!, BlockTreeLookupOptions.TotalDifficultyNotNeeded, 0).Returns(genesis);
        blockTree.IsMainChain(genesis).Returns(true);
        IStateSyncPivot inner = Substitute.For<IStateSyncPivot>();
        inner.GetPivotHeader().Returns(candidate);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.TerminalTotalDifficulty.Returns(UInt256.MaxValue);
        AuRaStateSyncPivot pivot = new(inner, blockTree, specProvider, LimboLogs.Instance);

        Assert.That(pivot.GetPivotHeader()?.Hash, Is.EqualTo(genesis.Hash));
    }

}
