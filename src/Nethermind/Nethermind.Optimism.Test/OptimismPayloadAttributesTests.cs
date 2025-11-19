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
        //TODO: source of expected results?
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
        var blockHeader = Build.A.BlockHeader.TestObject;
        var payloadAttributes = new OptimismPayloadAttributes
        {
            GasLimit = 1,
            Transactions = [],
            PrevRandao = Hash256.Zero,
            SuggestedFeeRecipient = TestItem.AddressA,
            EIP1559Params = Bytes.FromHexString(testCase.HexStringEIP1559Params)
        };

        Assert.That(payloadAttributes.GetPayloadId(blockHeader), Is.EqualTo(testCase.PayloadId));
    }

    private static IEnumerable<(int? length, Valid isValid)> Validate_EIP1559Params_TestCases()
    {
        yield return (null, Valid.Before(Spec.HoloceneTimeStamp));
        yield return (7, Valid.Never);
        yield return (8, Valid.Between(Spec.HoloceneTimeStamp, Spec.JovianTimeStamp));
        yield return (9, Valid.Never);
        yield return (15, Valid.Never);
        yield return (16, Valid.Since(Spec.JovianTimeStamp));
        yield return (17, Valid.Never);
    }

    [Test]
    public void Validate_EIP1559Params(
        [ValueSource(nameof(Validate_EIP1559Params_TestCases))] (int? length, Valid isValid) testCase,
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
            ParentBeaconBlockRoot = Hash256.Zero,
            Withdrawals = []
        };

        ISpecProvider spec = Spec.BuildFor(fork.Timestamp);

        Assert.That(
            payloadAttributes.Validate(spec, EngineApiVersions.Cancun, out var error),
            testCase.isValid.On(fork.Timestamp)
                ? Is.EqualTo(PayloadAttributesValidationResult.Success)
                : Is.EqualTo(PayloadAttributesValidationResult.InvalidPayloadAttributes),
            () => error!
        );
    }
}
