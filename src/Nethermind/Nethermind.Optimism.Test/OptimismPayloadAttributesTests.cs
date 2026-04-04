// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Optimism.Rpc;
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
        // V1: EIP1559Params is first 9 bytes (version + denom + elast), MinBaseFee is separate
        yield return ("0x010000000100000000", 0x0000000000000001UL, "0xb1be2b369ffc937d");
        yield return ("0x0100000001000001bc", 0x0000000000000abcUL, "0x3227f4be2903c6ec");
        yield return ("0x0100000001000001bc", 0x0000000000000defUL, "0xe471f88f2ef8553d");
        yield return ("0x0100000001ffffffff", 0x00000000ffffffffUL, "0xe56c5af8cb83c757");
        yield return ("0x01ffffffff00000000", 0xffffffff00000000UL, "0x34dec71cdbff4bbe");
        yield return ("0x01ffffffffffffffff", 0xffffffffffffffffUL, "0x2d20df1e01fc582a");
    }
    [TestCaseSource(nameof(PayloadIdTestCases))]
    public void Compute_PayloadID_with_EIP1559Params((string HexStringEIP1559Params, ulong? MinBaseFee, string PayloadId) testCase)
    {
        var blockHeader = Build.A.BlockHeader.TestObject;
        var payloadAttributes = new OptimismPayloadAttributes
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

    private static IEnumerable<(int? length, ulong? minBaseFee, Valid isValid)> Validate_EIP1559Params_TestCases()
    {
        yield return (null, null, Valid.Before(Spec.HoloceneTimeStamp));
        yield return (null, 0ul, Valid.Before(Spec.HoloceneTimeStamp));
        yield return (7, null, Valid.Never);
        yield return (7, 0ul, Valid.Never);
        yield return (8, null, Valid.Between(Spec.HoloceneTimeStamp, Spec.JovianTimeStamp));
        yield return (8, 0ul, Valid.Since(Spec.HoloceneTimeStamp));
        yield return (9, null, Valid.Never);
        yield return (9, 0ul, Valid.Never);
    }

    [Test]
    public void Validate_EIP1559Params(
        [ValueSource(nameof(Validate_EIP1559Params_TestCases))] (int? length, ulong? minBaseFee, Valid isValid) testCase,
        [ValueSource(typeof(Fork), nameof(Fork.AllAndNextToGenesis))] Fork fork
    )
    {
        var payloadAttributes = new OptimismPayloadAttributes
        {
            GasLimit = 1,
            Transactions = [],
            PrevRandao = Hash256.Zero,
            SuggestedFeeRecipient = TestItem.AddressA,
            Timestamp = fork.Timestamp,
            EIP1559Params = testCase.length is { } length ? new byte[length] : null,
            MinBaseFee = testCase.minBaseFee,
            ParentBeaconBlockRoot = Hash256.Zero,
            Withdrawals = []
        };

        ISpecProvider spec = Spec.BuildFor(fork.Timestamp);

        Assert.That(
            payloadAttributes.Validate(spec, EngineApiVersions.Fcu.V3, out var error),
            testCase.isValid.On(fork.Timestamp)
                ? Is.EqualTo(PayloadAttributesValidationResult.Success)
                : Is.EqualTo(PayloadAttributesValidationResult.InvalidPayloadAttributes),
            () => error!
        );
    }
}
