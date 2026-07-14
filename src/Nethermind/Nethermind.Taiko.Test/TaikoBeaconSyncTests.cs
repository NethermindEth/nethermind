// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Synchronization;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class TaikoBeaconSyncTests
{
    [TestCaseSource(nameof(ShouldBeInBeaconHeadersCases))]
    public bool ShouldBeInBeaconHeaders_HandlesBrokenBestSuggestedHeader(
        bool innerReturns,
        ulong? libhNumber,
        ulong bestSuggestedHeader,
        ulong headNumber,
        ulong pivotDestination,
        bool isKnownBlock,
        bool strictMode)
    {
        IBeaconSyncStrategy inner = Substitute.For<IBeaconSyncStrategy>();
        inner.ShouldBeInBeaconHeaders().Returns(innerReturns);

        IBlockTree blockTree = BlockTreeWith(libhNumber, bestSuggestedHeader, headNumber, isKnownBlock);
        IBeaconPivot beaconPivot = Substitute.For<IBeaconPivot>();
        beaconPivot.PivotDestinationNumber.Returns(pivotDestination);

        ISyncConfig syncConfig = new SyncConfig { StrictMode = strictMode };

        TaikoBeaconSync sut = new(inner, blockTree, beaconPivot, syncConfig, LimboLogs.Instance);
        return sut.ShouldBeInBeaconHeaders();
    }

    [Test]
    public void ShouldBeInBeaconHeaders_DelegatesFalse_WithoutTouchingBlockTree()
    {
        // Inner says no — decorator must short-circuit without re-checking. This guards
        // against accidental double-evaluation that would double-cost the BlockTree access.
        IBeaconSyncStrategy inner = Substitute.For<IBeaconSyncStrategy>();
        inner.ShouldBeInBeaconHeaders().Returns(false);

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IBeaconPivot beaconPivot = Substitute.For<IBeaconPivot>();
        ISyncConfig syncConfig = new SyncConfig();

        TaikoBeaconSync sut = new(inner, blockTree, beaconPivot, syncConfig, LimboLogs.Instance);

        Assert.That(sut.ShouldBeInBeaconHeaders(), Is.False);
        _ = blockTree.DidNotReceive().LowestInsertedBeaconHeader;
        _ = beaconPivot.DidNotReceive().PivotDestinationNumber;
    }

    [Test]
    public void OtherMembers_DelegateToInner()
    {
        IBeaconSyncStrategy inner = Substitute.For<IBeaconSyncStrategy>();
        inner.ShouldBeInBeaconModeControl().Returns(true);
        inner.IsBeaconSyncFinished(null).Returns(true);
        inner.MergeTransitionFinished.Returns(true);
        inner.GetTargetBlockHeight().Returns(123UL);
        inner.GetFinalizedHash().Returns(TestItem.KeccakA);
        inner.GetHeadBlockHash().Returns(TestItem.KeccakB);

        TaikoBeaconSync sut = new(
            inner,
            Substitute.For<IBlockTree>(),
            Substitute.For<IBeaconPivot>(),
            new SyncConfig(),
            LimboLogs.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(sut.ShouldBeInBeaconModeControl(), Is.True);
            Assert.That(sut.IsBeaconSyncFinished(null), Is.True);
            Assert.That(sut.MergeTransitionFinished, Is.True);
            Assert.That(sut.GetTargetBlockHeight(), Is.EqualTo(123UL));
            Assert.That(sut.GetFinalizedHash(), Is.EqualTo(TestItem.KeccakA));
            Assert.That(sut.GetHeadBlockHash(), Is.EqualTo(TestItem.KeccakB));
        });
    }

    private static IBlockTree BlockTreeWith(ulong? libhNumber, ulong bestSuggestedHeader, ulong headNumber, bool isKnownBlock)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        BlockHeader? libh = libhNumber is ulong l
            ? Build.A.BlockHeader.WithNumber(l).WithParent(Build.A.BlockHeader.WithNumber(l - 1).TestObject).TestObject
            : null;
        blockTree.LowestInsertedBeaconHeader.Returns(libh);
        blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithNumber(bestSuggestedHeader).TestObject);
        blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(headNumber).TestObject).TestObject);
        blockTree.IsKnownBlock(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(isKnownBlock);
        return blockTree;
    }

    private static IEnumerable<TestCaseData> ShouldBeInBeaconHeadersCases()
    {
        // Inner says we should not be in beacon headers. Decorator must agree.
        yield return new TestCaseData(false, (ulong?)100UL, 50UL, 50UL, 1UL, true, false)
            .Returns(false)
            .SetName("InnerSaysNo_DecoratorSaysNo");

        // Inner says yes but no LIBH yet — defer to inner (return true).
        yield return new TestCaseData(true, (ulong?)null, 0UL, 0UL, 1UL, false, false)
            .Returns(true)
            .SetName("LibhNull_DefersToInner");

        // THE TAIKO STALL CASE: Head advanced past pivot's previous target, BestSuggestedHeader
        // never bumped (broken on Taiko), LIBH stops at Head+1 because headers feed truncated
        // at the merge point, PivotDestinationNumber is below Head. Without the fix the inner
        // returns true here; the decorator must override to false using Head.Number.
        yield return new TestCaseData(true, (ulong?)17728UL, 0UL, 17727UL, 17664UL, true, false)
            .Returns(false)
            .SetName("TaikoStallCase_BestSuggestedHeaderZero_ButHeadAdvanced_OverridesToFalse");

        // LIBH walks all the way down to PivotDestinationNumber. This is the path the first
        // cold-start sync takes. Behavior must be the same as before the fix.
        yield return new TestCaseData(true, (ulong?)1UL, 0UL, 0UL, 1UL, true, false)
            .Returns(false)
            .SetName("ReachedDestination_FirstColdStartSync_NotInBeaconHeaders");

        // LIBH still above destination AND chain not merged (mid-sync). Decorator should
        // agree with inner that we're still in headers stage.
        yield return new TestCaseData(true, (ulong?)5000UL, 0UL, 0UL, 1UL, false, false)
            .Returns(true)
            .SetName("MidSync_StillNeedHeaders");

        // StrictMode disables the chainMerged shortcut. With LIBH > destination, the decorator
        // must NOT override even if Head reaches LIBH-1.
        yield return new TestCaseData(true, (ulong?)17728UL, 0UL, 17727UL, 17664UL, true, true)
            .Returns(true)
            .SetName("StrictMode_DoesNotShortCircuitOnChainMerged");

        // ParentHash not known: chainMerged cannot be true even if Head.Number is high enough.
        yield return new TestCaseData(true, (ulong?)17728UL, 0UL, 17727UL, 17664UL, false, false)
            .Returns(true)
            .SetName("ParentHashUnknown_NoOverride");

        // Mainnet-like case (BestSuggestedHeader correctly tracks Head). LIBH at Head+1,
        // BestSuggestedHeader == Head. The fix must not regress this — same behavior as
        // before (chainMerged is true via BestSuggestedHeader, so the override is a no-op).
        yield return new TestCaseData(true, (ulong?)17728UL, 17727UL, 17727UL, 17664UL, true, false)
            .Returns(false)
            .SetName("MainnetLikeCase_BestSuggestedHeaderTracksHead_StillReturnsFalse");
    }
}
