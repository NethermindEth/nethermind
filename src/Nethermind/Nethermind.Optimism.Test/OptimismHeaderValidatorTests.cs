// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class OptimismHeaderValidatorTests
{
    private static IEnumerable<string> ValidEIP1559ParametersExtraData()
    {
        yield return "0x000000000100000000";
        yield return "0x0000000001000001bc";
        yield return "0x0000000001ffffffff";
        yield return "0x00ffffffff00000000";
        yield return "0x00ffffffff000001bc";
        yield return "0x00ffffffffffffffff";
    }
    [TestCaseSource(nameof(ValidEIP1559ParametersExtraData))]
    public void Validates_EIP1559Parameters_InExtraData_AfterHolocene(string hexString)
    {
        var genesis = Build.A.BlockHeader
            .WithNumber(0)
            .WithTimestamp(1_000)
            .TestObject;
        var header = Build.A.BlockHeader
            .WithNumber(1)
            .WithTimestamp(2_000)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithExtraData(Bytes.FromHexString(hexString)).TestObject;

        var holoceneEnabledSpec = Substitute.For<ReleaseSpec>();
        holoceneEnabledSpec.IsOpHoloceneEnabled = true;

        var specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(header).Returns(holoceneEnabledSpec);

        var validator = new OptimismHeaderValidator(
            AlwaysPoS.Instance,
            Substitute.For<IBlockTree>(),
            Always.Valid,
            specProvider,
            TestLogManager.Instance);

        var valid = validator.Validate(header, genesis);

        valid.Should().BeTrue();
    }

    private static IEnumerable<string?> InvalidEIP1559ParametersExtraData()
    {
        yield return "0x0";
        yield return "0xffffaaaa";
        yield return "0x01ffffffff00000000";
        yield return "0xff0000000100000001";
        yield return "0x000000000000000001";
    }
    [TestCaseSource(nameof(InvalidEIP1559ParametersExtraData))]
    public void Rejects_EIP1559Parameters_InExtraData_AfterHolocene(string hexString)
    {
        var genesis = Build.A.BlockHeader
            .WithNumber(0)
            .WithTimestamp(1_000)
            .TestObject;
        var header = Build.A.BlockHeader
            .WithNumber(1)
            .WithTimestamp(2_000)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithExtraData(Bytes.FromHexString(hexString)).TestObject;

        var holoceneEnabledSpec = Substitute.For<ReleaseSpec>();
        holoceneEnabledSpec.IsOpHoloceneEnabled = true;

        var specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(header).Returns(holoceneEnabledSpec);

        var validator = new OptimismHeaderValidator(
            AlwaysPoS.Instance,
            Substitute.For<IBlockTree>(),
            Always.Valid,
            specProvider,
            TestLogManager.Instance);

        var valid = validator.Validate(header, genesis);

        valid.Should().BeFalse();
    }
}
