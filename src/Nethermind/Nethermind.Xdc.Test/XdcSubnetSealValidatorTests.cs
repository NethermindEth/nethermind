// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;
using System;

namespace Nethermind.Xdc.Test;

[Parallelizable(ParallelScope.All)]
public class XdcSubnetSealValidatorTests
{
    private const int Epoch = 900;
    private const int Gap = 450;

    [Test]
    public void EpochSwitch_PenaltiesInSnapshotButNotInHeader_HeaderIsStillValid()
    {
        (IXdcReleaseSpec _, ISpecProvider specProvider) = CreateSubnetSpec();
        Address[] candidates = [Address.FromNumber(1)];
        Address[] penalties = [Address.FromNumber(2)];
        ISubnetMasternodesCalculator calculator = Substitute.For<ISubnetMasternodesCalculator>();
        calculator.CalculateNextEpochMasternodes(Arg.Any<long>(), Arg.Any<Hash256>(), Arg.Any<IXdcReleaseSpec>()).Returns((candidates, penalties));
        XdcSubnetSealValidator validator = CreateValidator(specProvider, calculator, CreateEpochSwitchManager(true));
        XdcSubnetBlockHeader parent = BuildParentHeader(899);
        XdcSubnetBlockHeader header = BuildSubnetHeader(parent, 900, 110,
            b => b.WithValidators(candidates)
            .WithAuthor(candidates[0]));

        bool ok = validator.ValidateParams(parent, header, out string? error);

        Assert.That(ok, Is.True);
    }

    [Test]
    public void NonGapPlusOne_WithNextValidators_Invalid()
    {
        (IXdcReleaseSpec _, ISpecProvider specProvider) = CreateSubnetSpec();
        XdcSubnetSealValidator validator = CreateValidator(specProvider);

        XdcSubnetBlockHeader parent = BuildParentHeader(901);
        XdcSubnetBlockHeader header = BuildSubnetHeader(parent, 902, 110,
            b => b.WithNextValidators([Address.FromNumber(1)]));

        bool ok = validator.ValidateParams(parent, header, out string? error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("NextValidators"));
    }

    [Test]
    public void NonGapPlusOne_WithPenalties_Invalid()
    {
        (IXdcReleaseSpec _, ISpecProvider specProvider) = CreateSubnetSpec();
        XdcSubnetSealValidator validator = CreateValidator(specProvider);

        XdcSubnetBlockHeader parent = BuildParentHeader(901);
        XdcSubnetBlockHeader header = BuildSubnetHeader(parent, 902, 110,
            b => b.WithPenalties([Address.FromNumber(1)]));

        bool ok = validator.ValidateParams(parent, header, out string? error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("Penalties"));
    }

    [Test]
    public void GapPlusOne_NextValidatorsMismatch_Invalid()
    {
        (IXdcReleaseSpec _, ISpecProvider specProvider) = CreateSubnetSpec();
        Address[] candidates = [Address.FromNumber(1), Address.FromNumber(2)];

        ISubnetMasternodesCalculator calculator = Substitute.For<ISubnetMasternodesCalculator>();
        calculator.GetNextEpochCandidatesAndPenalties(Arg.Any<Hash256>()).Returns((candidates, Array.Empty<Address>()));

        XdcSubnetSealValidator validator = CreateValidator(specProvider, masternodesCalculator: calculator);

        XdcSubnetBlockHeader parent = BuildParentHeader(450);
        XdcSubnetBlockHeader header = BuildSubnetHeader(parent, 451, 110,
            b => b.WithNextValidators([Address.FromNumber(99)])); // wrong candidates

        bool ok = validator.ValidateParams(parent, header, out string? error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("NextValidators"));
    }

    [Test]
    public void GapPlusOne_PenaltiesMismatch_Invalid()
    {
        (IXdcReleaseSpec _, ISpecProvider specProvider) = CreateSubnetSpec();
        Address[] candidates = [Address.FromNumber(1)];
        Address[] penalties = [Address.FromNumber(2)];

        ISubnetMasternodesCalculator calculator = Substitute.For<ISubnetMasternodesCalculator>();
        calculator.GetNextEpochCandidatesAndPenalties(Arg.Any<Hash256>()).Returns((candidates, penalties));

        XdcSubnetSealValidator validator = CreateValidator(specProvider, masternodesCalculator: calculator);

        XdcSubnetBlockHeader parent = BuildParentHeader(450);
        // NextValidators matches but Penalties on header is empty while snapshot expects [addr2]
        XdcSubnetBlockHeader header = BuildSubnetHeader(parent, 451, 110,
            b => b.WithNextValidators(candidates));

        bool ok = validator.ValidateParams(parent, header, out string? error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("Penalties"));
    }

    [Test]
    public void ListsAreEqual_BothEmpty_ReturnsTrue()
    {
        Address[] a = [];
        Address[] b = [];

        Assert.That(a.ListsAreEqual(b), Is.True);
    }

    [Test]
    public void ListsAreEqual_SameElementsSameOrder_ReturnsTrue()
    {
        Address[] a = [Address.FromNumber(1), Address.FromNumber(2)];
        Address[] b = [Address.FromNumber(1), Address.FromNumber(2)];

        Assert.That(a.ListsAreEqual(b), Is.True);
    }

    [Test]
    public void ListsAreEqual_SameElementsDifferentOrder_ReturnsTrue()
    {
        Address[] a = [Address.FromNumber(1), Address.FromNumber(2)];
        Address[] b = [Address.FromNumber(2), Address.FromNumber(1)];

        Assert.That(a.ListsAreEqual(b), Is.True);
    }

    [Test]
    public void ListsAreEqual_DifferentCounts_ReturnsFalse()
    {
        Address[] a = [Address.FromNumber(1)];
        Address[] b = [Address.FromNumber(1), Address.FromNumber(2)];

        Assert.That(a.ListsAreEqual(b), Is.False);
    }

    [Test]
    public void ListsAreEqual_SameCountDifferentElements_ReturnsFalse()
    {
        Address[] a = [Address.FromNumber(1), Address.FromNumber(2)];
        Address[] b = [Address.FromNumber(1), Address.FromNumber(3)];

        Assert.That(a.ListsAreEqual(b), Is.False);
    }

    [Test]
    public void GapPlusOne_MatchingSnapshot_Valid()
    {
        (IXdcReleaseSpec _, ISpecProvider specProvider) = CreateSubnetSpec();
        Address[] candidates = [Address.FromNumber(1)];
        Address[] penalties = [Address.FromNumber(2)];

        ISubnetMasternodesCalculator calculator = Substitute.For<ISubnetMasternodesCalculator>();
        // Empty penalties so header's empty Penalties field also matches
        calculator.GetNextEpochCandidatesAndPenalties(Arg.Any<Hash256>()).Returns((candidates, penalties));

        XdcSubnetSealValidator validator = CreateValidator(specProvider, masternodesCalculator: calculator);

        XdcSubnetBlockHeader parent = BuildParentHeader(450);
        XdcSubnetBlockHeader header = BuildSubnetHeader(parent, 451, 110,
            b => b.WithNextValidators(candidates).WithPenalties(penalties));

        bool ok = validator.ValidateParams(parent, header, out string? error);

        Assert.That(ok, Is.True, error);
    }

    private static (IXdcReleaseSpec Spec, ISpecProvider SpecProvider) CreateSubnetSpec()
    {
        IXdcReleaseSpec releaseSpec = Substitute.For<IXdcReleaseSpec>();
        releaseSpec.EpochLength.Returns(Epoch);
        releaseSpec.Gap.Returns(Gap);
        releaseSpec.MinePeriod.Returns(10);
        releaseSpec.GasLimitBoundDivisor.Returns(1024);
        releaseSpec.When(x => x.ApplyV2Config(Arg.Any<ulong>())).Do(_ => { });

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);
        return (releaseSpec, specProvider);
    }

    // Returns a mock where IsEpochSwitchAtBlock → isEpochSwitch, and
    // GetEpochSwitchInfo → masternodes=[Address.Zero] so the leader check passes
    // when header.Author == Address.Zero (set in BuildSubnetHeader).
    private static IEpochSwitchManager CreateEpochSwitchManager(bool isEpochSwitch = false)
    {
        IEpochSwitchManager esm = Substitute.For<IEpochSwitchManager>();
        esm.IsEpochSwitchAtBlock(Arg.Any<XdcBlockHeader>()).Returns(isEpochSwitch);
        esm.GetEpochSwitchInfo(Arg.Any<XdcBlockHeader>())
            .Returns(new EpochSwitchInfo([Address.Zero], [], [], new BlockRoundInfo(Hash256.Zero, 0, 0)));
        return esm;
    }

    private static XdcSubnetSealValidator CreateValidator(
        ISpecProvider specProvider,
        ISubnetMasternodesCalculator? masternodesCalculator = null,
        IEpochSwitchManager? epochSwitchManager = null) => new(
            masternodesCalculator ?? Substitute.For<ISubnetMasternodesCalculator>(),
            epochSwitchManager ?? CreateEpochSwitchManager(),
            specProvider);

    private static XdcSubnetBlockHeader BuildParentHeader(long number)
    {
        XdcSubnetBlockHeaderBuilder b = Build.A.XdcSubnetBlockHeader();
        b.WithNumber(number);
        b.WithTimestamp(100);
        b.WithMixHash(Hash256.Zero);
        b.WithValidators(Array.Empty<Address>());
        b.WithPenalties(Array.Empty<byte>());
        b.WithNextValidators(Array.Empty<byte>());
        return b.TestObject;
    }

    private static XdcSubnetBlockHeader BuildSubnetHeader(
        BlockHeader parent,
        long number,
        ulong timestamp,
        Action<XdcSubnetBlockHeaderBuilder>? configure = null)
    {
        XdcSubnetBlockHeaderBuilder b = Build.A.XdcSubnetBlockHeader();
        b.WithParent(parent);
        b.WithNumber(number);
        b.WithTimestamp(timestamp);
        // BlockRound=2 > QC.ProposedBlockInfo.Round=1 passes the base-class round check
        b.WithExtraConsensusData(new ExtraFieldsV2(2, new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 1, 1), [], 450)));
        b.WithMixHash(Hash256.Zero);
        b.WithValidators(Array.Empty<Address>());
        b.WithPenalties(Array.Empty<byte>());
        b.WithNextValidators(Array.Empty<byte>());
        // currentLeaderIndex = BlockRound(2) % EpochLength(900) % masternodes.Length(1) = 0
        // masternodes[0] = Address.Zero in CreateEpochSwitchManager → must match header.Author
        b.WithAuthor(Address.Zero);
        configure?.Invoke(b);
        return b.TestObject;
    }
}
