// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

[Parallelizable(ParallelScope.All)]
public class OptimismPayloadAttributesTests
{
    private static IEnumerable<(string, string)> PayloadIdTestCases()
    {
        // V0
        yield return ("0x000000000100000000", "0x00dea77451f10b20");
        yield return ("0x0000000001000001bc", "0xf2975f6725d5f2e5");
        yield return ("0x0000000001ffffffff", "0x6b09fc2a90d6c067");
        yield return ("0x00ffffffff00000000", "0x9787e23f29594f18");
        yield return ("0x00ffffffff000001bc", "0x2cb414f72aac7824");
        yield return ("0x00ffffffffffffffff", "0xe411646692277df5");
        // V1
        yield return ("0x0100000001000000000000000000000001", "0xb1be2b369ffc937d");
        yield return ("0x0100000001000001bc0000000000000abc", "0x3227f4be2903c6ec");
        yield return ("0x0100000001000001bc0000000000000def", "0xe471f88f2ef8553d");
        yield return ("0x0100000001ffffffff00000000ffffffff", "0xe56c5af8cb83c757");
        yield return ("0x01ffffffff00000000ffffffff00000000", "0x34dec71cdbff4bbe");
        yield return ("0x01ffffffffffffffffffffffffffffffff", "0x2d20df1e01fc582a");
    }
    [TestCaseSource(nameof(PayloadIdTestCases))]
    public void Compute_PayloadID_with_EIP1559Params((string HexStringEIP1559Params, string PayloadId) testCase)
    {
        BlockHeader blockHeader = Build.A.BlockHeader.TestObject;
        OptimismPayloadAttributes payloadAttributes = new()
        {
            GasLimit = 1,
            Transactions = [],
            PrevRandao = Hash256.Zero,
            SuggestedFeeRecipient = TestItem.AddressA,
            EIP1559Params = Bytes.FromHexString(testCase.HexStringEIP1559Params)
        };

        Assert.That(payloadAttributes.GetPayloadId(blockHeader), Is.EqualTo(testCase.PayloadId));
    }

    [Test]
    public void Compute_PayloadID_includes_MinBaseFee()
    {
        BlockHeader blockHeader = Build.A.BlockHeader.TestObject;

        string payloadId = BuildAttributes(Spec.JovianTimeStamp, new byte[8], minBaseFee: 1).GetPayloadId(blockHeader);

        Assert.That(BuildAttributes(Spec.JovianTimeStamp, new byte[8], minBaseFee: 2).GetPayloadId(blockHeader), Is.Not.EqualTo(payloadId));
    }

    private static IEnumerable<(int? length, Valid isValid)> Validate_EIP1559Params_TestCases()
    {
        yield return (null, Valid.Before(Spec.HoloceneTimeStamp));
        yield return (7, Valid.Never);
        yield return (8, Valid.Since(Spec.HoloceneTimeStamp));
        yield return (9, Valid.Never);
        yield return (16, Valid.Never);
    }

    [Test]
    public void Validate_EIP1559Params(
        [ValueSource(nameof(Validate_EIP1559Params_TestCases))] (int? length, Valid isValid) testCase,
        [ValueSource(typeof(Fork), nameof(Fork.AllAndNextToGenesis))] Fork fork
    )
    {
        ulong? minBaseFee = fork.Timestamp >= Spec.JovianTimeStamp ? 0 : null;
        OptimismPayloadAttributes payloadAttributes = BuildAttributes(fork.Timestamp, testCase.length is { } length ? new byte[length] : null, minBaseFee);

        AssertValidation(payloadAttributes, fork.Timestamp, testCase.isValid);
    }

    private static IEnumerable<(ulong? minBaseFee, Valid isValid)> Validate_MinBaseFee_TestCases()
    {
        yield return (null, Valid.Before(Spec.JovianTimeStamp));
        yield return (0, Valid.Since(Spec.JovianTimeStamp));
        yield return (1_000_000, Valid.Since(Spec.JovianTimeStamp));
    }

    [Test]
    public void Validate_MinBaseFee(
        [ValueSource(nameof(Validate_MinBaseFee_TestCases))] (ulong? minBaseFee, Valid isValid) testCase,
        [ValueSource(typeof(Fork), nameof(Fork.AllAndNextToGenesis))] Fork fork
    )
    {
        byte[]? eip1559Params = fork.Timestamp >= Spec.HoloceneTimeStamp ? new byte[8] : null;
        OptimismPayloadAttributes payloadAttributes = BuildAttributes(fork.Timestamp, eip1559Params, testCase.minBaseFee);

        AssertValidation(payloadAttributes, fork.Timestamp, testCase.isValid);
    }

    /// <remarks>
    /// op-node and op-geth send an 8-byte <c>eip1559Params</c> and a separate <c>minBaseFee</c> encoded as a JSON number.
    /// </remarks>
    [Test]
    public void Accepts_Jovian_attributes_in_op_node_encoding()
    {
        string json = $$"""
            {
              "timestamp": "0x{{Spec.JovianTimeStamp:x}}",
              "prevRandao": "0x0000000000000000000000000000000000000000000000000000000000000000",
              "suggestedFeeRecipient": "0x4200000000000000000000000000000000000011",
              "withdrawals": [],
              "parentBeaconBlockRoot": "0x0000000000000000000000000000000000000000000000000000000000000000",
              "transactions": [],
              "noTxPool": true,
              "gasLimit": "0x1c9c380",
              "eip1559Params": "0x000000fa00000006",
              "minBaseFee": 1000000
            }
            """;

        OptimismPayloadAttributes payloadAttributes = new EthereumJsonSerializer().Deserialize<OptimismPayloadAttributes>(json)!;

        AssertValidation(payloadAttributes, Spec.JovianTimeStamp, Valid.Since(Spec.JovianTimeStamp));
        Assert.That(payloadAttributes.TryDecodeEIP1559Parameters(out EIP1559Parameters parameters, out _), Is.True);
        Assert.That(parameters, Is.EqualTo(new EIP1559Parameters(1, 250, 6, 1_000_000)));
    }

    private static OptimismPayloadAttributes BuildAttributes(ulong timestamp, byte[]? eip1559Params, ulong? minBaseFee) => new()
    {
        GasLimit = 1,
        Transactions = [],
        PrevRandao = Hash256.Zero,
        SuggestedFeeRecipient = TestItem.AddressA,
        Timestamp = timestamp,
        EIP1559Params = eip1559Params,
        MinBaseFee = minBaseFee,
        ParentBeaconBlockRoot = Hash256.Zero,
        Withdrawals = []
    };

    private static void AssertValidation(OptimismPayloadAttributes payloadAttributes, ulong timestamp, Valid isValid)
    {
        ISpecProvider spec = Spec.BuildFor(timestamp);

        Assert.That(
            payloadAttributes.Validate(spec, EngineApiVersions.Fcu.V3, out string? error),
            isValid.On(timestamp)
                ? Is.EqualTo(PayloadAttributesValidationResult.Success)
                : Is.EqualTo(PayloadAttributesValidationResult.InvalidPayloadAttributes),
            () => error!
        );
    }
}
