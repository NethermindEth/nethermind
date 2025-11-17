// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

[Parallelizable(ParallelScope.All)]
[TestFixtureSource(typeof(TestFixtures), nameof(TestFixtures.Forks))]
public class OptimismHeaderValidatorTests(ulong timestamp)
{
    private static readonly Hash256 PostCanyonWithdrawalsRoot = Keccak.OfAnEmptySequenceRlp;

    private (BlockHeader genesis, BlockHeader header) BuildHeaders(Action<BlockHeaderBuilder>? postBuild = null)
    {
        BlockHeader genesis = Build.A.BlockHeader
            .WithNumber(0)
            .WithTimestamp(Spec.GenesisTimestamp)
            .TestObject;

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
            .WithRequestsHash(timestamp >= Spec.IsthmusTimeStamp
                ? OptimismPostMergeBlockProducer.PostIsthmusRequestHash
                : null
            )
            .WithExtraDataHex(timestamp >= Spec.JovianTimeStamp
                ? "0x0100000001000001bc0000000000000000"
                : "0x0000000001000001bc"
            );

        postBuild?.Invoke(builder);

        return (genesis, builder.TestObject);
    }

    private static IEnumerable<(string data, Valid valid)> EIP1559ParametersExtraData => new (string data, Valid valid)[]
    {
        new("0x0100000001000000000000000000000000", Valid.Since(Spec.JovianTimeStamp)),
        new("0x0100000001000001bc0000000000000001", Valid.Since(Spec.JovianTimeStamp)),
        new("0x0100000001ffffffff00000000000000ff", Valid.Since(Spec.JovianTimeStamp)),
        new("0x01ffffffff0000000000000000000001ff", Valid.Since(Spec.JovianTimeStamp)),
        new("0x01ffffffff000001bc01000000000000ff", Valid.Since(Spec.JovianTimeStamp)),
        new("0x01ffffffffffffffffffffffffffffffff", Valid.Since(Spec.JovianTimeStamp)),

        new("0x000000000100000000", Valid.Between(Spec.HoloceneTimeStamp, Spec.JovianTimeStamp)),
        new("0x0000000001000001bc", Valid.Between(Spec.HoloceneTimeStamp, Spec.JovianTimeStamp)),
        new("0x0000000001ffffffff", Valid.Between(Spec.HoloceneTimeStamp, Spec.JovianTimeStamp)),
        new("0x00ffffffff00000000", Valid.Between(Spec.HoloceneTimeStamp, Spec.JovianTimeStamp)),
        new("0x00ffffffff000001bc", Valid.Between(Spec.HoloceneTimeStamp, Spec.JovianTimeStamp)),
        new("0x00ffffffffffffffff", Valid.Between(Spec.HoloceneTimeStamp, Spec.JovianTimeStamp)),

        new("0x0", Valid.Never),
        new("0xffffaaaa", Valid.Never),
        new("0x01ffffffff00000000", Valid.Never),
        new("0xff0000000100000001", Valid.Never),
        new("0x000000000000000000", Valid.Never),
        new("0x000000000000000001", Valid.Never),
        new("0x01ffffffff000001bc00000000000000", Valid.Never),
        new("0x01ffffffff000001bc000000000000000000", Valid.Never),
    }.Select(x =>
        (x.data, Valid.Before(Spec.HoloceneTimeStamp) | x.valid) // No validation before Holocene
    );

    [TestCaseSource(nameof(EIP1559ParametersExtraData))]
    public void Validates_EIP1559Parameters_InExtraData((string hexString, Valid valid) testCase)
    {
        (BlockHeader genesis, BlockHeader header) = BuildHeaders(b => b.WithExtraDataHex(testCase.hexString));

        var validator = new OptimismHeaderValidator(
            AlwaysPoS.Instance,
            Substitute.For<IBlockTree>(),
            Always.Valid, Spec.Instance, Spec.BuildFor(header),
            TestLogManager.Instance);

        Assert.That(() => validator.Validate(header, genesis), Is.EqualTo(testCase.valid.On(timestamp)));
    }

    private static IEnumerable<(Hash256? requestHash, Valid valid)> WithdrawalsRequestHashTestCases()
    {
        yield return (null, Valid.Before(Spec.IsthmusTimeStamp));
        yield return (TestItem.KeccakA, Valid.Before(Spec.IsthmusTimeStamp));
        yield return (OptimismPostMergeBlockProducer.PostIsthmusRequestHash, Valid.Always);
    }

    [TestCaseSource(nameof(WithdrawalsRequestHashTestCases))]
    public void ValidateRequestHash((Hash256? requestHash, Valid valid) testCase)
    {
        var (genesis, header) = BuildHeaders(b => b.WithRequestsHash(testCase.requestHash));

        var validator = new OptimismHeaderValidator(
            AlwaysPoS.Instance,
            Substitute.For<IBlockTree>(),
            Always.Valid, Spec.Instance, Spec.BuildFor(header),
            TestLogManager.Instance);

        string? error = null;
        Assert.That(() => validator.Validate(header, genesis, false, out error), Is.EqualTo(testCase.valid.On(timestamp)), () => error!);
    }

    private static IEnumerable<(long gasLimit, long gasUsed, long? blobGasUsed, Valid valid)> GasLimitTestCases()
    {
        yield return (1_000, 500, 0, Valid.Always);
        yield return (1_000, 1_000, 0, Valid.Always);
        yield return (1_000, 1_000, 500, Valid.Always);
        yield return (1_000, 1_000, 1_000, Valid.Always);

        yield return (1_000, 500, null, Valid.Never); // blobGasUsed missing
        yield return (1_000, 1_000, null, Valid.Never); // blobGasUsed missing

        yield return (1_000, 1_000 + 1, 500, Valid.Never); // gasUsed > gasLimit
        yield return (1_000, 1_000 + 1, 1_000, Valid.Never); // gasUsed > gasLimit
        yield return (1_000, 1_000 + 1, 1_000 + 1, Valid.Never); // blobGasUsed & gasUsed > gasLimit

        yield return (1_000, 1_000, 1_000 + 1, Valid.Before(Spec.JovianTimeStamp)); // blobGasUsed should be below gasLimit post Jovian
    }

    [TestCaseSource(nameof(GasLimitTestCases))]
    public void ValidateGasLimit((long gasLimit, long gasUsed, long? blobGasUsed, Valid valid) testCase)
    {
        (BlockHeader genesis, BlockHeader header) = BuildHeaders(b => b
            .WithGasLimit(testCase.gasLimit)
            .WithBlobGasUsed((ulong?)testCase.blobGasUsed)
            .WithGasUsed(testCase.gasUsed)
        );

        var validator = new OptimismHeaderValidator(
            AlwaysPoS.Instance,
            Substitute.For<IBlockTree>(),
            Always.Valid, Spec.Instance, Spec.BuildFor(header),
            TestLogManager.Instance);

        string? error = null;
        Assert.That(() => validator.Validate(header, genesis, false, out error), Is.EqualTo(testCase.valid.On(timestamp)), () => error!);
    }
}
