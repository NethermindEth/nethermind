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
        XdcBlockHeaderBuilder blockHeaderBuilder = CreateValidHeader(headerParent);

        yield return new object[]
        {
            headerParent,
            blockHeaderBuilder.WithHash(blockHeaderBuilder.TestObject.CalculateHash().ToHash256()).TestObject,
            true
        };

        blockHeaderBuilder = CreateValidHeader(headerParent);
        blockHeaderBuilder.WithValidator(Array.Empty<byte>());
        yield return new object[]
        {
            headerParent,
            blockHeaderBuilder.WithHash(blockHeaderBuilder.TestObject.CalculateHash().ToHash256()).TestObject,
            false
        };

        blockHeaderBuilder = CreateValidHeader(headerParent);
        blockHeaderBuilder.WithExtraData([]);
        yield return new object[]
        {
            headerParent,
            blockHeaderBuilder.WithHash(blockHeaderBuilder.TestObject.CalculateHash().ToHash256()).TestObject,
            false
        };

        blockHeaderBuilder = CreateValidHeader(headerParent);
        blockHeaderBuilder.WithNonce(XdcConstants.NonceDropVoteValue + 1);
        yield return new object[]
        {
            headerParent,
            blockHeaderBuilder.WithHash(blockHeaderBuilder.TestObject.CalculateHash().ToHash256()).TestObject,
            false
        };

        blockHeaderBuilder = CreateValidHeader(headerParent);
        blockHeaderBuilder.WithNonce(XdcConstants.NonceAuthVoteValue - 1);
        yield return new object[]
        {
            headerParent,
            blockHeaderBuilder.WithHash(blockHeaderBuilder.TestObject.CalculateHash().ToHash256()).TestObject,
            false
        };

        blockHeaderBuilder = CreateValidHeader(headerParent);
        blockHeaderBuilder.WithMixHash(Hash256.FromBytesWithPadding([0x01]));
        yield return new object[]
        {
            headerParent,
            blockHeaderBuilder.WithHash(blockHeaderBuilder.TestObject.CalculateHash().ToHash256()).TestObject,
            false
        };

        blockHeaderBuilder = CreateValidHeader(headerParent);
        blockHeaderBuilder.WithUnclesHash(Hash256.FromBytesWithPadding([0x01]));
        yield return new object[]
        {
            headerParent,
            blockHeaderBuilder.WithHash(blockHeaderBuilder.TestObject.CalculateHash().ToHash256()).TestObject,
            false
        };

        static XdcBlockHeaderBuilder CreateValidHeader(BlockHeader headerParent)
        {
            XdcBlockHeaderBuilder blockHeaderBuilder = (XdcBlockHeaderBuilder)Build.A
                .XdcBlockHeader()
                .WithGeneratedExtraConsensusData()
                .WithMixHash(Hash256.Zero)
                .WithParent(headerParent);
            blockHeaderBuilder.WithHash(new Hash256(blockHeaderBuilder.TestObject.CalculateHash()));
            return blockHeaderBuilder;
        }
    }

    [TestCaseSource(nameof(HeaderTestCases))]
    public void Validate_HeaderWithDifferentValues_ReturnsExpected(XdcBlockHeader parent, XdcBlockHeader header, bool expected)
    {
        ISealValidator sealValidator = Substitute.For<ISealValidator>();
        sealValidator.ValidateSeal(Arg.Any<BlockHeader>(), Arg.Any<bool>()).Returns(true);
        sealValidator.ValidateParams(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<bool>()).Returns(true);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec releaseSpec = Substitute.For<IXdcReleaseSpec>();
        releaseSpec.GasLimitBoundDivisor.Returns(1);
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);
        XdcHeaderValidator validator = new(Substitute.For<IBlockTree>(), sealValidator, specProvider, Substitute.For<ILogManager>());

        Assert.That(validator.Validate(header, parent, false, out string? error), Is.EqualTo(expected));
    }
}
