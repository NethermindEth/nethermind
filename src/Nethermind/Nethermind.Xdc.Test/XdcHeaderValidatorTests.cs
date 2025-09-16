// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Xdc.Spec;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Nethermind.Xdc.Test;

public class Tests
{
    [Test]
    public void Validate_NotAnXdcHeader_ThrowsArgumentException()
    {
        var header = Build.A.BlockHeader.TestObject;
        XdcHeaderValidator validator = new(Substitute.For<IBlockTree>(), Substitute.For<ISealValidator>(), Substitute.For<ISpecProvider>(), Substitute.For<ILogManager>());

        Assert.That(() => validator.Validate(header, null, false, out string? error), Throws.TypeOf<ArgumentException>());
    }

    public static IEnumerable<object[]> HeaderTestCases()
    {
        BlockHeader headerParent = Build.A
            .XdcBlockHeader()
            .WithMixHash(Hash256.Zero)
            .TestObject;
        XdcBlockHeaderBuilder blockHeaderBuilder = CreateValidHeader();

        yield return new object[]
        {
            blockHeaderBuilder,
            true
        };

        blockHeaderBuilder = CreateValidHeader();
        blockHeaderBuilder.WithValidator(Array.Empty<byte>());
        yield return new object[]
        {
            blockHeaderBuilder,
            false
        };

        blockHeaderBuilder = CreateValidHeader();
        blockHeaderBuilder.WithExtraData([]);
        yield return new object[]
        {
            blockHeaderBuilder,
            false
        };

        blockHeaderBuilder = CreateValidHeader();
        blockHeaderBuilder.WithNonce(XdcConstants.NonceDropVoteValue + 1);
        yield return new object[]
        {
            blockHeaderBuilder,
            false
        };

        blockHeaderBuilder = CreateValidHeader();
        blockHeaderBuilder.WithNonce(XdcConstants.NonceAuthVoteValue - 1);
        yield return new object[]
        {
            blockHeaderBuilder,
            false
        };

        blockHeaderBuilder = CreateValidHeader();
        blockHeaderBuilder.WithMixHash(Hash256.FromBytesWithPadding([0x01]));
        yield return new object[]
        {
            blockHeaderBuilder,
            false
        };

        blockHeaderBuilder = CreateValidHeader();
        blockHeaderBuilder.WithUnclesHash(Hash256.FromBytesWithPadding([0x01]));
        yield return new object[]
        {
            blockHeaderBuilder,
            false
        };

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

        ISealValidator sealValidator = Substitute.For<ISealValidator>();
        sealValidator.ValidateSeal(Arg.Any<BlockHeader>(), Arg.Any<bool>()).Returns(true);
        sealValidator.ValidateParams(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<bool>()).Returns(true);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec releaseSpec = Substitute.For<IXdcReleaseSpec>();
        releaseSpec.GasLimitBoundDivisor.Returns(1);
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);
        XdcHeaderValidator validator = new(Substitute.For<IBlockTree>(), sealValidator, specProvider, Substitute.For<ILogManager>());

        Assert.That(validator.Validate(headerBuilder.TestObject, headerParent, false, out string? error), Is.EqualTo(expected));
    }
}
