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
    private static IEnumerable<(string, ulong?, string)> PayloadIdTestCases()
    {
        // V0
        yield return ("0x000000000100000000", null, "0x00dea77451f10b20");
        yield return ("0x0000000001000001bc", null, "0xf2975f6725d5f2e5");
        yield return ("0x0000000001ffffffff", null, "0x6b09fc2a90d6c067");
        yield return ("0x00ffffffff00000000", null, "0x9787e23f29594f18");
        yield return ("0x00ffffffff000001bc", null, "0x2cb414f72aac7824");
        yield return ("0x00ffffffffffffffff", null, "0xe411646692277df5");
        // 8-byte params with MinBaseFee
        yield return ("0x0000000100000000", 1UL, "0x45b296ba4fd598c5");
        yield return ("0x00000001000001bc", 0xabcUL, "0x4846b5d9cecbff6b");
        yield return ("0x00000001000001bc", 0xdefUL, "0x5ea21df353c54513");
        yield return ("0x00000001ffffffff", 0xffffffffUL, "0x50ed8e906952abd0");
        yield return ("0xffffffff00000000", 0xffffffff00000000UL, "0x86b5c4e46610022b");
        yield return ("0xffffffffffffffff", ulong.MaxValue, "0x5713beee480922da");
    }

    [TestCaseSource(nameof(PayloadIdTestCases))]
    public void Compute_PayloadID_with_EIP1559Params((string HexStringEIP1559Params, ulong? MinBaseFee, string PayloadId) testCase)
    {
        BlockHeader blockHeader = Build.A.BlockHeader.TestObject;
        OptimismPayloadAttributes payloadAttributes = new()
        {
            GasLimit = 1,
            Transactions = [],
            PrevRandao = Hash256.Zero,
            SuggestedFeeRecipient = TestItem.AddressA,
            EIP1559Params = Bytes.FromHexString(testCase.HexStringEIP1559Params),
            MinBaseFee = testCase.MinBaseFee
        };

        Assert.That(payloadAttributes.GetPayloadId(blockHeader), Is.EqualTo(testCase.PayloadId));
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

    [TestCase(Spec.HoloceneTimeStamp, null)]
    [TestCase(Spec.JovianTimeStamp, 1UL)]
    public void Validate_EIP1559Params_rejects_zero_elasticity_with_non_zero_denominator(ulong timestamp, ulong? minBaseFee)
    {
        OptimismPayloadAttributes payloadAttributes = BuildAttributes(timestamp, Bytes.FromHexString("0x0000000800000000"), minBaseFee);

        Assert.That(
            payloadAttributes.Validate(Spec.BuildFor(timestamp), EngineApiVersions.Fcu.V3, out string? error),
            Is.EqualTo(PayloadAttributesValidationResult.InvalidPayloadAttributes));
        Assert.That(error, Does.Contain("elasticity cannot be 0"));
    }
}
