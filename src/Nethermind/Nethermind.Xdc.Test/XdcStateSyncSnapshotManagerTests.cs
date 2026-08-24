// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Xdc.Contracts;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Xdc.Test.Helpers;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Test.Builders;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.Test;

internal class XdcStateSyncSnapshotManagerTests
{
    // Gap blocks reach XdcConstants.LimitPenaltyEpoch epochs back from the pivot's epoch switch,
    // floored at the first epoch of the chain — which is what clamps every case but the last.
    [
        TestCase(24UL, 10UL, 5UL, new int[] { 0, 9, 18, 23 }, new int[] { 5, 15 }),
        TestCase(25UL, 10UL, 5UL, new int[] { 0, 9, 18, 23 }, new int[] { 5, 15, 25 }),
        TestCase(26UL, 10UL, 5UL, new int[] { 0, 9, 18, 23 }, new int[] { 5, 15, 25 }),
        TestCase(26UL, 10UL, 5UL, new int[] { 0, 9, 18, 28 }, new int[] { 5, 15, 25 }),
        TestCase(11UL, 10UL, 5UL, new int[] { 0, 9 }, new int[] { 5 }),
        TestCase(4UL, 10UL, 5UL, new int[] { 0 }, new int[] { }),
        // Chain long enough for the full lookback: the whole penalty window, not just the pivot's epoch.
        TestCase(26UL, 5UL, 2UL, new int[] { 0, 5, 10, 15, 20, 25 }, new int[] { 3, 8, 13, 18, 23 }),
    ]
    public async Task GetGapBlocks_ReturnsExpectedGapBlockNumbers(
        ulong pivotNumber,
        ulong epochLength,
        ulong gap,
        int[] epochSwitchNumbers,
        int[] expectedGapBlockNumbers
    )
    {
        XdcTestBlockchain xdcTestBlockchain = await XdcTestBlockchain.Create(pivotNumber);
        xdcTestBlockchain.ChangeReleaseSpec(spec =>
        {
            spec.EpochLength = epochLength;
            spec.Gap = gap;
        });

        XdcBlockHeader pivotHeader = (XdcBlockHeader)xdcTestBlockchain.BlockTree.FindHeader(pivotNumber)!;

        Assert.That(pivotHeader.Number, Is.EqualTo(pivotNumber));

        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        IMasternodeVotingContract masternodeVotingContract = Substitute.For<IMasternodeVotingContract>();
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        epochSwitchManager.IsEpochSwitchAtBlock(Arg.Any<XdcBlockHeader>()).Returns(number => epochSwitchNumbers.Contains((int)number.Arg<XdcBlockHeader>().Number));

        XdcStateSyncSnapshotManager manager = new(
            xdcTestBlockchain.SpecProvider,
            epochSwitchManager,
            xdcTestBlockchain.BlockTree,
            snapshotManager,
            masternodeVotingContract
        );

        XdcBlockHeader[] result = manager.GetGapBlocks(pivotHeader);

        int[] resultNumbers = result.Select(r => (int)r.Number).ToArray();

        Assert.That(resultNumbers, Is.EqualTo(expectedGapBlockNumbers));
    }

    // The penalty lookback is floored at the switch block, so gapBlockNum = switchBlock - gap here.
    // V1 branch triggers when gapBlockNum + gap == switchBlock
    [TestCase(27UL, 10UL, 10UL, 5UL, new int[] { 10, 19 }, new int[] { 15, 25 })]
    [TestCase(14UL, 10UL, 10UL, 5UL, new int[] { 10 }, new int[] { })]
    public async Task GetGapBlocks_WhenGapLandsOnSwitchBlock_StoresV1Snapshot(
        ulong pivotNumber,
        ulong switchBlock,
        ulong epochLength,
        ulong gap,
        int[] epochSwitchNumbers,
        int[] expectedGapBlockNumbers
    )
    {
        XdcTestBlockchain xdcTestBlockchain = await XdcTestBlockchain.Create(pivotNumber);
        xdcTestBlockchain.ChangeReleaseSpec(spec =>
        {
            spec.EpochLength = epochLength;
            spec.Gap = gap;
            spec.SwitchBlock = switchBlock;
        });

        Address[] masternodeAddresses = { TestItem.AddressA, TestItem.AddressB };
        XdcBlockHeader switchHeader = (XdcBlockHeader)xdcTestBlockchain.BlockTree.FindHeader(switchBlock)!;
        switchHeader.ExtraData = XdcTestHelper.BuildV1ExtraData(masternodeAddresses);

        XdcBlockHeader pivotHeader = (XdcBlockHeader)xdcTestBlockchain.BlockTree.FindHeader(pivotNumber)!;
        Assert.That(pivotHeader.Number, Is.EqualTo(pivotNumber));

        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        IMasternodeVotingContract masternodeVotingContract = Substitute.For<IMasternodeVotingContract>();
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        epochSwitchManager.IsEpochSwitchAtBlock(Arg.Any<XdcBlockHeader>()).Returns(ci => epochSwitchNumbers.Contains((int)ci.Arg<XdcBlockHeader>().Number));

        XdcStateSyncSnapshotManager manager = new(
            xdcTestBlockchain.SpecProvider,
            epochSwitchManager,
            xdcTestBlockchain.BlockTree,
            snapshotManager,
            masternodeVotingContract
        );

        XdcBlockHeader[] result = manager.GetGapBlocks(pivotHeader);
        int[] resultNumbers = result.Select(r => (int)r.Number).ToArray();

        Assert.That(resultNumbers, Is.EqualTo(expectedGapBlockNumbers));
        snapshotManager.Received(1).StoreSnapshot(Arg.Is<Snapshot>(s =>
            s.BlockNumber == switchBlock - gap &&
            s.NextEpochCandidates.SequenceEqual(masternodeAddresses)));
    }
}
