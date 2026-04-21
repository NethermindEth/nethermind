// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
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
        specProvider.GetSpec(Arg.Any<BlockHeader>()).Returns(releaseSpec);
        return (releaseSpec, specProvider);
    }

    private static XdcSubnetSealValidator CreateValidator(
        ISpecProvider specProvider,
        ISnapshotManager? snapshotManager = null,
        ISubnetMasternodesCalculator? masternodesCalculator = null)
    {
        return new XdcSubnetSealValidator(
            masternodesCalculator ?? Substitute.For<ISubnetMasternodesCalculator>(),
            Substitute.For<IEpochSwitchManager>(),
            specProvider);
    }

    private static XdcSubnetBlockHeader BuildSubnetHeader(BlockHeader parent, long number, ulong timestamp, Action<XdcSubnetBlockHeaderBuilder>? configure = null)
    {
        XdcSubnetBlockHeaderBuilder b = Build.A.XdcSubnetBlockHeader();
        b.WithParent(parent);
        b.WithNumber(number);
        b.WithTimestamp(timestamp);
        b.WithGeneratedExtraConsensusData();
        b.WithMixHash(Hash256.Zero);
        b.WithValidators(Array.Empty<Address>());
        b.WithPenalties(Array.Empty<byte>());
        b.WithNextValidators(Array.Empty<byte>());
        configure?.Invoke(b);
        return b.TestObject;
    }

    [Test]
    public void NonGapPlusOne_WithNextValidators_Invalid()
    {
        (IXdcReleaseSpec _, ISpecProvider specProvider) = CreateSubnetSpec();
        XdcSubnetSealValidator validator = CreateValidator(specProvider);

        XdcSubnetBlockHeader parent = (XdcSubnetBlockHeader)Build.A.XdcSubnetBlockHeader()
            .WithNextValidators(Array.Empty<byte>())
            .WithNumber(901)
            .WithTimestamp(100)
            .WithGeneratedExtraConsensusData()
            .WithMixHash(Hash256.Zero)
            .WithValidators(Array.Empty<Address>())
            .WithPenalties(Array.Empty<byte>())
            .TestObject;

        XdcSubnetBlockHeader header = BuildSubnetHeader(parent, 902, 110, b => b.WithNextValidators([Address.FromNumber(1)]));

        bool ok = validator.Validate(header, parent, false, out string? error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("NextValidators"));
    }

    [Test]
    public void GapPlusOne_NextValidatorsMismatch_Invalid()
    {
        (IXdcReleaseSpec _, ISpecProvider specProvider) = CreateSubnetSpec();
        Address[] candidates = [Address.FromNumber(1), Address.FromNumber(2)];
        Address[] penalties = [Address.FromNumber(3)];
        SubnetSnapshot snapshot = new(450, Keccak.Compute("gap"), candidates, penalties);

        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        snapshotManager.GetSnapshotByGapNumber(450).Returns(snapshot);

        XdcSubnetSealValidator validator = CreateValidator(specProvider, snapshotManager);

        XdcSubnetBlockHeader parent = (XdcSubnetBlockHeader)Build.A.XdcSubnetBlockHeader()
            .WithNumber(450)
            .WithTimestamp(100)
            .WithNextValidators(Array.Empty<byte>())
            .WithGeneratedExtraConsensusData()
            .WithMixHash(Hash256.Zero)
            .WithValidators(Array.Empty<Address>())
            .WithPenalties(Array.Empty<byte>())
            .TestObject;

        XdcSubnetBlockHeader header = BuildSubnetHeader(parent, 451, 110, b =>
        {
            b.WithNextValidators([Address.FromNumber(99)]);
            b.WithPenalties(penalties);
        });

        bool ok = validator.Validate(header, parent, false, out string? error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("NextValidators"));
    }

    [Test]
    public void GapPlusOne_PenaltiesMismatch_Invalid()
    {
        (IXdcReleaseSpec _, ISpecProvider specProvider) = CreateSubnetSpec();
        Address[] candidates = [Address.FromNumber(1)];
        Address[] penalties = [Address.FromNumber(2)];
        SubnetSnapshot snapshot = new(450, Keccak.Compute("gap"), candidates, penalties);

        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        snapshotManager.GetSnapshotByGapNumber(450).Returns(snapshot);

        XdcSubnetSealValidator validator = CreateValidator(specProvider, snapshotManager);

        XdcSubnetBlockHeader parent = (XdcSubnetBlockHeader)Build.A.XdcSubnetBlockHeader()
            .WithNumber(450)
            .WithNextValidators(Array.Empty<byte>())
            .WithTimestamp(100)
            .WithGeneratedExtraConsensusData()
            .WithMixHash(Hash256.Zero)
            .WithValidators(Array.Empty<Address>())
            .WithPenalties(Array.Empty<byte>())
            .TestObject;

        XdcSubnetBlockHeader header = BuildSubnetHeader(parent, 451, 110, b =>
        {
            b.WithNextValidators(candidates);
            b.WithPenalties([Address.FromNumber(99)]);
        });

        bool ok = validator.Validate(header, parent, false, out string? error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("Penalties"));
    }

    [Test]
    public void GapPlusOne_MatchingSnapshot_Valid()
    {
        (IXdcReleaseSpec _, ISpecProvider specProvider) = CreateSubnetSpec();
        Address[] candidates = [Address.FromNumber(1)];
        Address[] penalties = [Address.FromNumber(2)];
        SubnetSnapshot snapshot = new(450, Keccak.Compute("gap"), candidates, penalties);

        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        snapshotManager.GetSnapshotByGapNumber(450).Returns(snapshot);

        XdcSubnetSealValidator validator = CreateValidator(specProvider, snapshotManager);

        XdcSubnetBlockHeader parent = (XdcSubnetBlockHeader)Build.A.XdcSubnetBlockHeader()
            .WithNumber(450)
            .WithNextValidators(Array.Empty<byte>())
            .WithTimestamp(100)
            .WithGeneratedExtraConsensusData()
            .WithMixHash(Hash256.Zero)
            .WithValidators(Array.Empty<Address>())
            .WithPenalties(Array.Empty<byte>())
            .TestObject;

        XdcSubnetBlockHeader header = BuildSubnetHeader(parent, 451, 110, b =>
        {
            b.WithNextValidators(candidates);
            b.WithPenalties(penalties);
        });

        bool ok = validator.Validate(header, parent, false, out string? error);

        Assert.That(ok, Is.True, error);
    }

    [Test]
    public void SubnetEpochSwitch_InvalidNonce_Invalid()
    {
        (IXdcReleaseSpec _, ISpecProvider specProvider) = CreateSubnetSpec();
        IMasternodesCalculator calculator = Substitute.For<IMasternodesCalculator>();
        XdcSubnetSealValidator validator = CreateValidator(specProvider, masternodesCalculator: calculator);

        XdcSubnetBlockHeader parent = (XdcSubnetBlockHeader)Build.A.XdcSubnetBlockHeader()
            .WithNumber(899)
            .WithNextValidators(Array.Empty<byte>())
            .WithTimestamp(100)
            .WithGeneratedExtraConsensusData()
            .WithMixHash(Hash256.Zero)
            .WithValidators(Array.Empty<Address>())
            .WithPenalties(Array.Empty<byte>())
            .TestObject;

        XdcSubnetBlockHeader header = BuildSubnetHeader(parent, 900, 110, b =>
        {
            b.WithNonce(XdcConstants.NonceAuthVoteValue);
            b.WithValidators(Array.Empty<Address>());
        });

        bool ok = validator.Validate(header, parent, false, out string? error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("nonce").IgnoreCase);
        calculator.DidNotReceive().CalculateNextEpochMasternodes(Arg.Any<long>(), Arg.Any<Hash256>(), Arg.Any<IXdcReleaseSpec>());
    }

    [Test]
    public void SubnetEpochSwitch_EmptyValidators_Invalid()
    {
        (IXdcReleaseSpec _, ISpecProvider specProvider) = CreateSubnetSpec();
        XdcSubnetSealValidator validator = CreateValidator(specProvider);

        XdcSubnetBlockHeader parent = (XdcSubnetBlockHeader)Build.A.XdcSubnetBlockHeader()
            .WithNumber(899)
            .WithNextValidators(Array.Empty<byte>())
            .WithTimestamp(100)
            .WithGeneratedExtraConsensusData()
            .WithMixHash(Hash256.Zero)
            .WithValidators(Array.Empty<Address>())
            .WithPenalties(Array.Empty<byte>())
            .TestObject;

        XdcSubnetBlockHeader header = BuildSubnetHeader(parent, 900, 110, b =>
        {
            b.WithNonce(XdcConstants.NonceDropVoteValue);
            b.WithValidators(Array.Empty<Address>());
        });

        bool ok = validator.Validate(header, parent, false, out string? error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("validators").IgnoreCase);
    }
}
