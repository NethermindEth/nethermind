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
    private const ulong GenesisTimestamp = 1_000;
    //private const ulong BedrockBlockNumber = 1_100;
    //private const ulong RegolithTimestamp = 1_200;
    private const ulong CanyonTimestamp = 1_300;
    private const ulong HoloceneTimeStamp = 2_000;
    private const ulong IsthmusTimeStamp = 2_100;

    private static readonly Hash256 PostCanyonWithdrawalsRoot = Keccak.OfAnEmptySequenceRlp;

    private static readonly IOptimismSpecHelper Spec =
        new OptimismSpecHelper(new OptimismChainSpecEngineParameters
        {
            CanyonTimestamp = CanyonTimestamp,
            HoloceneTimestamp = HoloceneTimeStamp,
            IsthmusTimestamp = IsthmusTimeStamp
        });

    private static IEnumerable<(string, bool)> EIP1559ParametersExtraData()
    {
        // Valid
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
        yield return ("0x000000000000000000", false);
        yield return ("0x000000000000000001", false);
        yield return ("0x00ffffffff000001bc00", false);
    }

    [TestCaseSource(nameof(EIP1559ParametersExtraData))]
    public void Validates_EIP1559Parameters_InExtraData_AfterHolocene((string HexString, bool IsValid) testCase)
    {
        var genesis = Build.A.BlockHeader
            .WithNumber(0)
            .WithTimestamp(GenesisTimestamp)
            .TestObject;
        var header = Build.A.BlockHeader
            .WithNumber(1)
            .WithTimestamp(HoloceneTimeStamp)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithWithdrawalsRoot(PostCanyonWithdrawalsRoot)
            .WithExtraData(Bytes.FromHexString(testCase.HexString)).TestObject;

        var validator = new OptimismHeaderValidator(
            AlwaysPoS.Instance,
            Substitute.For<IBlockTree>(),
            Always.Valid,
            Spec,
            Substitute.For<ISpecProvider>(),
            TestLogManager.Instance);

        var valid = validator.Validate(header, genesis);

        valid.Should().Be(testCase.IsValid);
    }

    [TestCaseSource(nameof(EIP1559ParametersExtraData))]
    public void Ignores_ExtraData_BeforeHolocene((string HexString, bool _) testCase)
    {
        var genesis = Build.A.BlockHeader
            .WithNumber(0)
            .WithTimestamp(GenesisTimestamp)
            .TestObject;
        var header = Build.A.BlockHeader
            .WithNumber(1)
            .WithTimestamp(HoloceneTimeStamp - 1)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithWithdrawalsRoot(PostCanyonWithdrawalsRoot)
            .WithExtraData(Bytes.FromHexString(testCase.HexString)).TestObject;

        var validator = new OptimismHeaderValidator(
            AlwaysPoS.Instance,
            Substitute.For<IBlockTree>(),
            Always.Valid,
            Spec,
            Substitute.For<ISpecProvider>(),
            TestLogManager.Instance);

        var valid = validator.Validate(header, genesis);

        valid.Should().BeTrue();
    }

    [TestCaseSource(nameof(WithdrawalsRootData))]
    public void WithdrawalRoot_Validity(ulong timestamp, Hash256? withdrawalHash, bool isValid)
    {
        var genesis = Build.A.BlockHeader
            .WithNumber(0)
            .WithTimestamp(GenesisTimestamp)
            .TestObject;

        var header = Build.A.BlockHeader
            .WithNumber(1)
            .WithTimestamp(timestamp)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithExtraData(Bytes.FromHexString("0x00ffffffffffffffff"))
            .WithWithdrawalsRoot(withdrawalHash)
            .TestObject;

        var validator = new OptimismHeaderValidator(
            AlwaysPoS.Instance,
            Substitute.For<IBlockTree>(),
            Always.Valid,
            Spec,
            Substitute.For<ISpecProvider>(),
            TestLogManager.Instance);

        var valid = validator.Validate(header, genesis);

        valid.Should().Be(isValid);
    }

    private static IEnumerable<TestCaseData> WithdrawalsRootData()
    {
        yield return new TestCaseData(CanyonTimestamp - 1, null, true).SetName("Pre Canyon");
        yield return new TestCaseData(CanyonTimestamp, Keccak.OfAnEmptySequenceRlp, true).SetName("Post Canyon");
        yield return new TestCaseData(CanyonTimestamp, null, false).SetName("Post Canyon - invalid");
        yield return new TestCaseData(IsthmusTimeStamp, Keccak.EmptyTreeHash, true).SetName("Post Isthmus");
        yield return new TestCaseData(IsthmusTimeStamp, null, false).SetName("Post Isthmus - invalid");
    }
}
