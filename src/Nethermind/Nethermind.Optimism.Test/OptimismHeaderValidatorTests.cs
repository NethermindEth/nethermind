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

[Parallelizable(ParallelScope.All)]
public class OptimismHeaderValidatorTests
{
    private static IEnumerable<(string, bool)> EIP1559ParametersExtraData()
    {
        // Valid
        yield return ("0x000000000000000000", true);
        yield return ("0x000000000100000000", true);
        yield return ("0x0000000001000001bc", true);
        yield return ("0x0000000001ffffffff", true);
        yield return ("0x00ffffffff00000000", true);
        yield return ("0x00ffffffff000001bc", true);
        yield return ("0x00ffffffffffffffff", true);
        // Invalid
        yield return ("0x0", false);
        yield return ("0xffffaaaa", false);
        yield return ("0x01ffffffff00000000", false);
        yield return ("0xff0000000100000001", false);
        yield return ("0x000000000000000001", false);
    }

    [TestCaseSource(nameof(EIP1559ParametersExtraData))]
    public void Validates_EIP1559Parameters_InExtraData_AfterHolocene((string HexString, bool IsValid) testCase)
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
            .WithExtraData(Bytes.FromHexString(testCase.HexString)).TestObject;

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

        valid.Should().Be(testCase.IsValid);
    }

    [TestCaseSource(nameof(EIP1559ParametersExtraData))]
    public void Ignores_ExtraData_BeforeHolocene((string HexString, bool _) testCase)
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
            .WithExtraData(Bytes.FromHexString(testCase.HexString)).TestObject;

        var holoceneDisabledSpec = Substitute.For<ReleaseSpec>();
        holoceneDisabledSpec.IsOpHoloceneEnabled = false;

        var specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(header).Returns(holoceneDisabledSpec);

        var validator = new OptimismHeaderValidator(
            AlwaysPoS.Instance,
            Substitute.For<IBlockTree>(),
            Always.Valid,
            specProvider,
            TestLogManager.Instance);

        var valid = validator.Validate(header, genesis);

        valid.Should().BeTrue();
    }
}
