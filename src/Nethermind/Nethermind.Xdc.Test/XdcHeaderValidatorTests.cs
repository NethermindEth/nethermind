// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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
using System.Collections.Generic;

namespace Nethermind.Xdc.Test;

[Parallelizable(ParallelScope.All)]
public class XdcHeaderValidatorTests
{
    [Test]
    public void Validate_NotAnXdcHeader_ThrowsArgumentException()
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        BlockHeader header = Build.A.BlockHeader.WithParent(parent).TestObject;
        XdcHeaderValidator validator = new(Substitute.For<IBlockTree>(), Substitute.For<IQuorumCertificateManager>(), Substitute.For<ISealValidator>(), Substitute.For<ISpecProvider>(), Substitute.For<ILogManager>());

        Assert.That(() => validator.Validate(header, parent, false, out _), Throws.TypeOf<ArgumentException>());
    }

    public static IEnumerable<TestCaseData> HeaderTestCases()
    {
        XdcBlockHeaderBuilder blockHeaderBuilder = CreateValidHeader();

        yield return new TestCaseData(blockHeaderBuilder, true).SetName("Valid header");

        blockHeaderBuilder = CreateValidHeader().WithValidator([]);
        yield return new TestCaseData(blockHeaderBuilder, false).SetName("Missing block seal");

        blockHeaderBuilder = CreateValidHeader();
        blockHeaderBuilder.WithExtraData([]);
        yield return new TestCaseData(blockHeaderBuilder, false).SetName("No consensus data");

        blockHeaderBuilder = CreateValidHeader();
        blockHeaderBuilder.WithNonce(XdcConstants.NonceDropVoteValue + 1);
        yield return new TestCaseData(blockHeaderBuilder, false).SetName("Invalid nonce above drop vote");

        blockHeaderBuilder = CreateValidHeader();
        blockHeaderBuilder.WithNonce(XdcConstants.NonceAuthVoteValue - 1);
        yield return new TestCaseData(blockHeaderBuilder, false).SetName("Invalid nonce below auth vote");

        blockHeaderBuilder = CreateValidHeader();
        blockHeaderBuilder.WithMixHash(Hash256.FromBytesWithPadding([0x01]));
        yield return new TestCaseData(blockHeaderBuilder, false).SetName("Invalid mix hash");

        blockHeaderBuilder = CreateValidHeader();
        blockHeaderBuilder.WithUnclesHash(Hash256.FromBytesWithPadding([0x01]));
        yield return new TestCaseData(blockHeaderBuilder, false).SetName("Invalid uncles hash");

        blockHeaderBuilder = CreateValidHeader();
        blockHeaderBuilder.WithDifficulty(2);
        yield return new TestCaseData(blockHeaderBuilder, false).SetName("Invalid difficulty");

        blockHeaderBuilder = CreateValidHeader();
        blockHeaderBuilder.WithDifficulty(2);
        blockHeaderBuilder.WithTotalDifficulty(1);
        yield return new TestCaseData(blockHeaderBuilder, false).SetName("Invalid total difficulty");

        static XdcBlockHeaderBuilder CreateValidHeader()
        {
            XdcBlockHeaderBuilder blockHeaderBuilder = (XdcBlockHeaderBuilder)Build.A
                .XdcBlockHeader()
                .WithGeneratedExtraConsensusData()
                .WithMixHash(Hash256.Zero);
            return blockHeaderBuilder;
        }
    }

    [TestCaseSource(nameof(HeaderTestCases))]
    public void Validate_HeaderWithDifferentValues_ReturnsExpected(XdcBlockHeaderBuilder headerBuilder, bool expected)
    {
        BlockHeader headerParent = Build.A
            .XdcBlockHeader()
            .WithMixHash(Hash256.Zero)
            .TestObject;
        headerBuilder.WithParent(headerParent);

        XdcHeaderValidator validator = CreateValidator(false, false);

        Assert.That(validator.Validate(headerBuilder.TestObject, headerParent, false, out string? error), Is.EqualTo(expected), "Error was: " + error);
    }

    [Test]
    public void Validate_HeaderWithoutEthereumRoots_IgnoresEnabledEips([Values] bool withdrawalsEnabled, [Values] bool beaconRootEnabled)
    {
        BlockHeader parent = Build.A.XdcBlockHeader().WithMixHash(Hash256.Zero).TestObject;
        BlockHeader header = Build.A.XdcBlockHeader()
            .WithGeneratedExtraConsensusData()
            .WithMixHash(Hash256.Zero)
            .WithParent(parent)
            .TestObject;
        XdcHeaderValidator validator = CreateValidator(withdrawalsEnabled, beaconRootEnabled);

        bool valid = validator.Validate(header, parent, false, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.WithdrawalsRoot, Is.Null);
            Assert.That(header.ParentBeaconBlockRoot, Is.Null);
            Assert.That(valid, Is.True, error);
        }
    }

    private static XdcHeaderValidator CreateValidator(bool withdrawalsEnabled, bool beaconRootEnabled)
    {
        ISealValidator sealValidator = Substitute.For<ISealValidator>();
        sealValidator.ValidateSeal(Arg.Any<BlockHeader>(), Arg.Any<bool>()).Returns(true);
        sealValidator.ValidateParams(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<bool>()).Returns(true);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec releaseSpec = Substitute.For<IXdcReleaseSpec>();
        releaseSpec.IsEip4895Enabled.Returns(withdrawalsEnabled);
        releaseSpec.IsEip4788Enabled.Returns(beaconRootEnabled);
        releaseSpec.GasLimitBoundDivisor.Returns(1UL);
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);
        IQuorumCertificateManager quorumCertificateManager = Substitute.For<IQuorumCertificateManager>();
        quorumCertificateManager.VerifyCertificate(Arg.Any<QuorumCertificate>(), Arg.Any<XdcBlockHeader>(), out _).Returns(true);
        return new XdcHeaderValidator(Substitute.For<IBlockTree>(), quorumCertificateManager, sealValidator, specProvider, Substitute.For<ILogManager>());
    }
}
