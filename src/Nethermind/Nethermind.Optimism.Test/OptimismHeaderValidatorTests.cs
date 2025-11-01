// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

[Parallelizable(ParallelScope.All)]
public class OptimismHeaderValidatorTests
{
    private static readonly Hash256 PostCanyonWithdrawalsRoot = Keccak.OfAnEmptySequenceRlp;

    [TestCaseSource(nameof(EIP1559ParametersExtraData))]
    public void Validates_EIP1559Parameters_InExtraData_AfterHolocene_OrJovian((string HexString, bool validHolocene, bool validJovian) testCase)
    {
        var genesis = Build.A.BlockHeader
            .WithNumber(0)
            .WithTimestamp(Spec.GenesisTimestamp)
            .TestObject;

        var headerHolocene = BuildHeader(Spec.HoloceneTimeStamp);
        var headerJovian = BuildHeader(Spec.JovianTimeStamp);

        var validator = new OptimismHeaderValidator(
            AlwaysPoS.Instance,
            Substitute.For<IBlockTree>(),
            Always.Valid, Spec.Instance,
            Spec.BuildFor(headerHolocene, headerJovian),
            TestLogManager.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => validator.Validate(headerHolocene, genesis), Is.EqualTo(testCase.validHolocene));
            Assert.That(() => validator.Validate(headerJovian, genesis), Is.EqualTo(testCase.validJovian));
        }

        BlockHeader BuildHeader(ulong timestamp)
        {
            BlockHeaderBuilder builder = Build.A.BlockHeader
                .WithNumber(1)
                .WithParent(genesis)
                .WithTimestamp(timestamp)
                .WithDifficulty(0)
                .WithNonce(0)
                .WithBlobGasUsed(0)
                .WithExcessBlobGas(0)
                .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
                .WithWithdrawalsRoot(PostCanyonWithdrawalsRoot)
                .WithExtraData(Bytes.FromHexString(testCase.HexString))
                .WithRequestsHash(timestamp >= Spec.IsthmusTimeStamp
                    ? OptimismPostMergeBlockProducer.PostIsthmusRequestHash
                    : null
                );

            return builder.TestObject;
        }
    }

    [TestCaseSource(nameof(EIP1559ParametersExtraData))]
    public void Ignores_ExtraData_BeforeHolocene((string HexString, bool _1, bool _2) testCase)
    {
        var genesis = Build.A.BlockHeader
            .WithNumber(0)
            .WithTimestamp(Spec.GenesisTimestamp)
            .TestObject;
        var header = Build.A.BlockHeader
            .WithNumber(1)
            .WithParent(genesis)
            .WithTimestamp(Spec.HoloceneTimeStamp - 1)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithWithdrawalsRoot(PostCanyonWithdrawalsRoot)
            .WithExtraData(Bytes.FromHexString(testCase.HexString))
            .TestObject;

        var validator = new OptimismHeaderValidator(
            AlwaysPoS.Instance,
            Substitute.For<IBlockTree>(),
            Always.Valid, Spec.Instance,
            Spec.BuildFor(header),
            TestLogManager.Instance);

        var valid = validator.Validate(header, genesis);

        valid.Should().BeTrue();
    }

    private static IEnumerable<TestCaseData> WithdrawalsRequestHashTestCases()
    {
        yield return new TestCaseData(Spec.CanyonTimestamp - 1, null, true)
            .SetName("Pre Canyon - null request hash");
        yield return new TestCaseData(Spec.CanyonTimestamp - 1, null, true)
            .SetName("Pre Canyon - some request hash");

        yield return new TestCaseData(Spec.CanyonTimestamp, null, true)
            .SetName("Post Canyon - null request hash");
        yield return new TestCaseData(Spec.CanyonTimestamp, TestItem.KeccakA, true)
            .SetName("Post Canyon - some request hash");

        yield return new TestCaseData(Spec.IsthmusTimeStamp, OptimismPostMergeBlockProducer.PostIsthmusRequestHash, true)
            .SetName("Post Isthmus - expected request hash");
        yield return new TestCaseData(Spec.IsthmusTimeStamp, null, false).SetName(
            "Post Isthmus - invalid request hash");
    }

    [TestCaseSource(nameof(WithdrawalsRequestHashTestCases))]
    public void ValidateRequestHash(ulong timestamp, Hash256? requestHash, bool isValid)
    {
        var genesis = Build.A.BlockHeader
            .WithNumber(0)
            .WithTimestamp(Spec.GenesisTimestamp)
            .TestObject;

        var header = Build.A.BlockHeader
            .WithNumber(1)
            .WithParent(genesis)
            .WithTimestamp(timestamp)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithExtraData(Bytes.FromHexString("0x00ffffffffffffffff"))
            .WithRequestsHash(requestHash)
            .TestObject;

        var validator = new OptimismHeaderValidator(
            AlwaysPoS.Instance,
            Substitute.For<IBlockTree>(),
            Always.Valid, Spec.Instance,
            Spec.BuildFor(header),
            TestLogManager.Instance);

        var valid = validator.Validate(header, genesis);

        valid.Should().Be(isValid);
    }

    private static IEnumerable<(string, bool, bool)> EIP1559ParametersExtraData()
    {
        // Valid post Holocene
        yield return ("0x000000000100000000", true, false);
        yield return ("0x0000000001000001bc", true, false);
        yield return ("0x0000000001ffffffff", true, false);
        yield return ("0x00ffffffff00000000", true, false);
        yield return ("0x00ffffffff000001bc", true, false);
        yield return ("0x00ffffffffffffffff", true, false);

        // Valid post Jovian
        yield return ("0x0100000001000000000000000000000000", false, true);
        yield return ("0x0100000001000001bc0000000000000001", false, true);
        yield return ("0x0100000001ffffffff00000000000000ff", false, true);
        yield return ("0x01ffffffff0000000000000000000001ff", false, true);
        yield return ("0x01ffffffff000001bc01000000000000ff", false, true);
        yield return ("0x01ffffffffffffffffffffffffffffffff", false, true);

        // Invalid
        yield return ("0x0", false, false);
        yield return ("0xffffaaaa", false, false);
        yield return ("0x01ffffffff00000000", false, false);
        yield return ("0xff0000000100000001", false, false);
        yield return ("0x000000000000000000", false, false);
        yield return ("0x000000000000000001", false, false);
        yield return ("0x01ffffffff000001bc00000000000000", false, false);
        yield return ("0x01ffffffff000001bc000000000000000000", false, false);
    }

    [TestCaseSource(nameof(GasLimitTestCases))]
    public void ValidateGasLimit_AfterHolocene_OrJovian((long gasLimit, long gasUsed, long? blobGasUsed, ulong timestamp, bool valid) testCase)
    {
        BlockHeader genesis = Build.A.BlockHeader
            .WithNumber(0)
            .WithTimestamp(Spec.GenesisTimestamp)
            .TestObject;

        BlockHeader header = Build.A.BlockHeader
            .WithNumber(1)
            .WithParent(genesis)
            .WithTimestamp(testCase.timestamp)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithGasLimit(testCase.gasLimit)
            .WithBlobGasUsed((ulong?)testCase.blobGasUsed)
            .WithGasUsed(testCase.gasUsed)
            .WithExcessBlobGas(0)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithExtraDataHex(testCase.timestamp >= Spec.JovianTimeStamp
                ? "0x01ffffffffffffffffffffffffffffffff"
                : "0x00ffffffffffffffff"
            )
            .WithRequestsHash(testCase.timestamp >= Spec.IsthmusTimeStamp
                ? OptimismPostMergeBlockProducer.PostIsthmusRequestHash
                : null
            )
            .TestObject;

        var validator = new OptimismHeaderValidator(
            AlwaysPoS.Instance,
            Substitute.For<IBlockTree>(),
            Always.Valid, Spec.Instance,
            Spec.BuildFor(header),
            TestLogManager.Instance);

        string? error = null;
        Assert.That(() => validator.Validate(header, genesis, false, out error), Is.EqualTo(testCase.valid), () => error!);
    }

    private static IEnumerable<(long gasLimit, long gasUsed, long? blobGasUsed, ulong timestamp, bool valid)> GasLimitTestCases()
    {
        yield return (1_000_000, 1_000, null, Spec.HoloceneTimeStamp, false); // blobGasUsed missing
        yield return (1_000_000, 1_000, null, Spec.JovianTimeStamp, false); // blobGasUsed missing
        yield return (1_000_000, 1_000, 0, Spec.HoloceneTimeStamp, true);
        yield return (1_000_000, 1_000, 1_000, Spec.HoloceneTimeStamp, true); // blobGasUsed != 0
        yield return (1_000_000, 1_000, 0, Spec.JovianTimeStamp, true);
        yield return (1_000_000, 1_000, 1_000, Spec.JovianTimeStamp, true);
        yield return (1_000_000, 1_000_000, 1_000_000, Spec.JovianTimeStamp, true);
        yield return (1_000_000, 1_000_000 + 1, 0, Spec.HoloceneTimeStamp, false); // gasUsed > gasLimit
        yield return (1_000_000, 1_000_000 + 1, 1_000, Spec.JovianTimeStamp, false); // gasUsed > gasLimit
        yield return (1_000_000, 1_000, 1_000_000 + 1, Spec.JovianTimeStamp, false); // blobGasUsed > gasLimit
        yield return (1_000_000, 1_000_000 + 1, 1_000_000 + 1, Spec.JovianTimeStamp, false); // blobGasUsed & gasUsed > gasLimit
    }
}
