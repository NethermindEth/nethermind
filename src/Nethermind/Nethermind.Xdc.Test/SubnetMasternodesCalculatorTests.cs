// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using FluentAssertions;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

internal class SubnetMasternodesCalculatorTests
{
    [Test]
    public void CalculateNextEpochMasternodes_FiltersPenaltiesAndRespectsMaxMasternodes()
    {
        IXdcReleaseSpec spec = Substitute.For<IXdcReleaseSpec>();
        spec.MaxMasternodes.Returns(3);

        Address a1 = Address.FromNumber(1);
        Address a2 = Address.FromNumber(2);
        Address a3 = Address.FromNumber(3);
        Address a4 = Address.FromNumber(4);
        Address a5 = Address.FromNumber(5);
        Address a6 = Address.FromNumber(6);

        SubnetSnapshot snapshot = new(0, Hash256.Zero, [a1, a2, a3, a4, a5, a6], [a1, a2]);

        ISubnetSnapshotManager snapshotManager = Substitute.For<ISubnetSnapshotManager>();
        snapshotManager.GetSnapshotByBlockNumber(Arg.Any<long>(), Arg.Any<IXdcReleaseSpec>()).Returns(snapshot);

        SubnetMasternodesCalculator calculator = new(snapshotManager);

        (Address[] masternodes, Address[] penalties) = calculator.CalculateNextEpochMasternodes(0, Hash256.Zero, spec);

        masternodes.Should().Equal(a3, a4, a5);
        penalties.Should().Equal(a1, a2);
    }

    [Test]
    public void GetNextEpochCandidatesAndPenalties_ReturnsSnapshotValues()
    {
        Address c1 = Address.FromNumber(1);
        Address c2 = Address.FromNumber(2);
        Address p1 = Address.FromNumber(100);

        Hash256 parentHash = Keccak.Compute("parent");
        SubnetSnapshot snapshot = new(0, parentHash, [c1, c2], [p1]);

        ISubnetSnapshotManager snapshotManager = Substitute.For<ISubnetSnapshotManager>();
        snapshotManager.GetSnapshotByHash(parentHash).Returns(snapshot);

        SubnetMasternodesCalculator calculator = new(snapshotManager);

        (Address[] nextEpochCandidates, Address[] nextPenalties) = calculator.GetNextEpochCandidatesAndPenalties(parentHash);

        nextEpochCandidates.Should().BeSameAs(snapshot.NextEpochCandidates);
        nextPenalties.Should().BeSameAs(snapshot.NextEpochPenalties);
    }
}
