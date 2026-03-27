// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using EraVerificationException = Nethermind.Era1.Exceptions.EraVerificationException;
using Nethermind.EraE.Proofs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.EraE.Test.Proofs;

public class ValidatorTests
{
    [Test]
    public void VerifyAccumulator_WithMatchingTrustedAccumulator_ReturnsTrue()
    {
        ValueHash256 root = new("0xaabbccdd00000000000000000000000000000000000000000000000000000000");
        Validator sut = BuildValidator(root);

        bool result = sut.VerifyAccumulator(0, root);

        result.Should().BeTrue();
    }

    [Test]
    public void VerifyAccumulator_WithMismatchedTrustedAccumulator_ReturnsFalse()
    {
        ValueHash256 root = new("0xaabbccdd00000000000000000000000000000000000000000000000000000000");
        Validator sut = BuildValidator(root);

        bool result = sut.VerifyAccumulator(0,
            new ValueHash256("0x1122334400000000000000000000000000000000000000000000000000000000"));

        result.Should().BeFalse();
    }

    [Test]
    public void VerifyAccumulator_WhenNoTrustedAccumulatorsProvided_ReturnsTrue()
    {
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.BeaconChainGenesisTimestamp.Returns((ulong?)1606824023UL);
        Validator sut = new(specProvider, null, null, null);

        bool result = sut.VerifyAccumulator(0,
            new ValueHash256("0xaabbccdd00000000000000000000000000000000000000000000000000000000"));

        result.Should().BeTrue();
    }

    [Test]
    public void VerifyAccumulator_WhenAccumulatorNotFoundForEpoch_ThrowsEraVerificationException()
    {
        ValueHash256 root = new("0xaabbccdd00000000000000000000000000000000000000000000000000000000");
        Validator sut = BuildValidator(root);

        // Epoch 1 requires index 1 in the trusted set, but we only have 1 entry (index 0)
        sut.Invoking(v => v.VerifyAccumulator(8192, root))
            .Should().Throw<EraVerificationException>();
    }

    private static Validator BuildValidator(ValueHash256 trustedRoot)
    {
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.BeaconChainGenesisTimestamp.Returns((ulong?)1606824023UL);
        return new Validator(specProvider, new List<ValueHash256> { trustedRoot }, null, null);
    }
}
