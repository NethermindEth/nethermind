// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Test.ChainSpecStyle;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

[Parallelizable(ParallelScope.All)]
[TestFixtureSource(nameof(HeaderValidationForks))]
public class OptimismHeaderValidatorTests(Fork fork)
{
    private readonly ulong _timestamp = fork.Timestamp;

    private static readonly Hash256 PostCanyonWithdrawalsRoot = Keccak.OfAnEmptySequenceRlp;

    private static IEnumerable<Fork> HeaderValidationForks => Fork.AllAndNextToGenesis.Concat(
    [
        new("Before Canyon", Spec.CanyonTimestamp - 1),
        new("Before Ecotone", Spec.EcotoneTimestamp - 1)
    ]);

    private (BlockHeader genesis, BlockHeader header) BuildHeaders(Action<BlockHeaderBuilder>? postBuild = null)
    {
        BlockHeader genesis = Build.A.BlockHeader
            .WithNumber(0)
            .WithTimestamp(Spec.GenesisTimestamp)
            .TestObject;

        BlockHeaderBuilder builder = Build.A.BlockHeader
            .WithNumber(1)
            .WithParent(genesis)
            .WithTimestamp(_timestamp)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithParentBeaconBlockRoot(_timestamp >= Spec.EcotoneTimestamp ? Keccak.Zero : null)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithWithdrawalsRoot(_timestamp >= Spec.CanyonTimestamp ? PostCanyonWithdrawalsRoot : null)
            .WithRequestsHash(_timestamp >= Spec.IsthmusTimeStamp
                ? OptimismPostMergeBlockProducer.PostIsthmusRequestHash
                : null
            )
            .WithExtraDataHex(_timestamp >= Spec.JovianTimeStamp
                ? "0x0100000001000001bc0000000000000000"
                : "0x0000000001000001bc"
            );

        postBuild?.Invoke(builder);

        return (genesis, builder.TestObject);
    }

    [Test]
    public void Validate_header_root_presence_matches_fork(
        [Values] bool withdrawalsRoot,
        [Values] bool present,
        [Values] bool timestampSpec)
    {
        (BlockHeader genesis, BlockHeader header) = BuildHeaders(b =>
        {
            if (withdrawalsRoot)
                b.WithWithdrawalsRoot(present ? PostCanyonWithdrawalsRoot : null);
            else
                b.WithParentBeaconBlockRoot(present ? Keccak.Zero : null);
        });

        ISpecProvider specProvider = timestampSpec
            ? Spec.BuildFor(genesis.Timestamp, _timestamp)
            : Spec.BuildFor(genesis, header);
        ChainSpec chainSpec = new()
        {
            Genesis = Build.A.Block.WithHeader(genesis).TestObject,
            Parameters = new ChainParameters(),
            Allocations = [],
            EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(
                new OptimismChainSpecEngineParameters { BedrockBlockNumber = 0 })
        };
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(chainSpec))
            .AddModule(new OptimismModule(chainSpec, new OptimismConfig()))
            .AddSingleton<ISpecProvider>(specProvider)
            .AddSingleton<IOptimismSpecHelper>(Spec.Instance)
            .Build();
        IHeaderValidator validator = container.Resolve<IHeaderValidator>();

        bool enabled = _timestamp >= (withdrawalsRoot ? Spec.CanyonTimestamp : Spec.EcotoneTimestamp);
        string? expectedError = present == enabled ? null : withdrawalsRoot
            ? enabled ? BlockErrorMessages.MissingWithdrawalsRoot : BlockErrorMessages.WithdrawalsRootNotEnabled
            : enabled ? BlockErrorMessages.MissingParentBeaconBlockRoot : BlockErrorMessages.ParentBeaconBlockRootNotEnabled;

        bool valid = validator.Validate(header, genesis, false, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.EqualTo(present == enabled), () => error!);
            Assert.That(error, Is.EqualTo(expectedError));
        }
    }

    private static IEnumerable<(string data, Valid isValid)> EIP1559ParametersExtraData => new (string data, Valid isValid)[]
    {
        new("0x0100000001000000000000000000000000", Valid.Never), // zero elasticity
        new("0x0100000001000001bc0000000000000001", Valid.Since(Spec.JovianTimeStamp)),
        new("0x0100000001ffffffff00000000000000ff", Valid.Since(Spec.JovianTimeStamp)),
        new("0x01ffffffff0000000000000000000001ff", Valid.Never), // zero elasticity
        new("0x01ffffffff000001bc01000000000000ff", Valid.Since(Spec.JovianTimeStamp)),
        new("0x01ffffffffffffffffffffffffffffffff", Valid.Since(Spec.JovianTimeStamp)),

        new("0x000000000100000000", Valid.Never), // zero elasticity
        new("0x0000000001000001bc", Valid.Between(Spec.HoloceneTimeStamp, Spec.JovianTimeStamp)),
        new("0x0000000001ffffffff", Valid.Between(Spec.HoloceneTimeStamp, Spec.JovianTimeStamp)),
        new("0x00ffffffff00000000", Valid.Never), // zero elasticity
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
        (x.data, Valid.Before(Spec.HoloceneTimeStamp) | x.isValid) // No validation before Holocene
    );

    [TestCaseSource(nameof(EIP1559ParametersExtraData))]
    public void Validates_EIP1559Parameters_InExtraData((string hexString, Valid isValid) testCase)
    {
        (BlockHeader genesis, BlockHeader header) = BuildHeaders(b => b.WithExtraDataHex(testCase.hexString));

        OptimismHeaderValidator validator = new(
            AlwaysPoS.Instance,
            Substitute.For<IBlockTree>(),
            Always.Valid, Spec.Instance, Spec.BuildFor(header),
            TestLogManager.Instance);

        Assert.That(() => validator.Validate(header, genesis), Is.EqualTo(testCase.isValid.On(_timestamp)));
    }

    private static IEnumerable<(Hash256? requestHash, Valid isValid)> WithdrawalsRequestHashTestCases()
    {
        yield return (null, Valid.Before(Spec.IsthmusTimeStamp));
        yield return (TestItem.KeccakA, Valid.Before(Spec.IsthmusTimeStamp));
        yield return (OptimismPostMergeBlockProducer.PostIsthmusRequestHash, Valid.Always);
    }

    [TestCaseSource(nameof(WithdrawalsRequestHashTestCases))]
    public void ValidateRequestHash((Hash256? requestHash, Valid isValid) testCase)
    {
        (BlockHeader? genesis, BlockHeader? header) = BuildHeaders(b => b.WithRequestsHash(testCase.requestHash));

        OptimismHeaderValidator validator = new(
            AlwaysPoS.Instance,
            Substitute.For<IBlockTree>(),
            Always.Valid, Spec.Instance, Spec.BuildFor(header),
            TestLogManager.Instance);

        string? error = null;
        Assert.That(() => validator.Validate(header, genesis, false, out error), Is.EqualTo(testCase.isValid.On(_timestamp)), () => error!);
    }

    private static IEnumerable<TestCaseData> GasLimitTestCases()
    {
        yield return new(1_000UL, 500UL, 0, Valid.Always);
        yield return new(1_000UL, 1_000UL, 0, Valid.Always);
        yield return new(1_000UL, 1_000UL, 500, Valid.Always);
        yield return new(1_000UL, 1_000UL, 1_000, Valid.Always);

        yield return new TestCaseData(1_000UL, 500UL, null, Valid.Never).SetName("blobGasUsed missing");
        yield return new TestCaseData(1_000UL, 1_000UL, null, Valid.Never).SetName("blobGasUsed missing, gasUsed = gasLimit");

        yield return new TestCaseData(1_000UL, 1_000UL + 1UL, 500, Valid.Never).SetName("gasUsed > gasLimit");
        yield return new TestCaseData(1_000UL, 1_000UL + 1UL, 1_000, Valid.Never).SetName("gasUsed > gasLimit, blobGasUsed = gasLimit");
        yield return new TestCaseData(1_000UL, 1_000UL + 1UL, 1_000 + 1, Valid.Never).SetName("blobGasUsed & gasUsed > gasLimit");

        yield return new TestCaseData(1_000UL, 1_000UL, 1_000 + 1, Valid.Before(Spec.JovianTimeStamp)).SetName("blobGasUsed > gasLimit post Jovian");
    }

    [TestCaseSource(nameof(GasLimitTestCases))]
    public void ValidateGasLimit(ulong gasLimit, ulong gasUsed, int? blobGasUsed, Valid isValid)
    {
        (BlockHeader genesis, BlockHeader header) = BuildHeaders(b => b
            .WithGasLimit(gasLimit)
            .WithBlobGasUsed((ulong?)blobGasUsed)
            .WithGasUsed(gasUsed)
        );

        OptimismHeaderValidator validator = new(
            AlwaysPoS.Instance,
            Substitute.For<IBlockTree>(),
            Always.Valid, Spec.Instance, Spec.BuildFor(header),
            TestLogManager.Instance);

        string? error = null;
        Assert.That(() => validator.Validate(header, genesis, false, out error), Is.EqualTo(isValid.On(_timestamp)), () => error!);
    }
}
