// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Xdc.Test.Helpers;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;

internal class XdcStateSyncSnapshotManagerTests
{

    [
        TestCase(24, 10, 5, new int[] { 0, 9, 18, 23 }, new int[] { 15 }),
        TestCase(25, 10, 5, new int[] { 0, 9, 18, 23 }, new int[] { 15, 25 }),
        TestCase(26, 10, 5, new int[] { 0, 9, 18, 23 }, new int[] { 15, 25 }),
        TestCase(26, 10, 5, new int[] { 0, 9, 18, 28 }, new int[] { 5, 15, 25 }),
    ]
    public async Task GetGapBlocks_ReturnsExpectedGapBlockNumbers(
        int pivotNumber,
        int epochLength,
        int gap,
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

        pivotHeader.Number.Should().Be(pivotNumber);

        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        IMasternodeVotingContract masternodeVotingContract = Substitute.For<IMasternodeVotingContract>();
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        epochSwitchManager.IsEpochSwitchAtBlock(Arg.Any<XdcBlockHeader>()).Returns(number => epochSwitchNumbers.Contains((int)number.Arg<XdcBlockHeader>().Number));

        XdcStateSyncSnapshotManager manager = new XdcStateSyncSnapshotManager(
            xdcTestBlockchain.SpecProvider,
            epochSwitchManager,
            xdcTestBlockchain.BlockTree,
            snapshotManager,
            masternodeVotingContract
        );

        XdcBlockHeader[] result = manager.GetGapBlocks(pivotHeader);

        int[] resultNumbers = result.Select(r => (int)r.Number).ToArray();

        resultNumbers.Should().BeEquivalentTo(expectedGapBlockNumbers);
    }

}
