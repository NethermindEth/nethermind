// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Optimism.Rpc;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

[Parallelizable(ParallelScope.All)]
public class EIP1559ParametersTests
{
    private static IEnumerable<(string hexString, EIP1559Parameters expected)> DecodeBlockHeaderParametersCases()
    {
        // V0
        yield return ("0x000000000000000000", new(0, 0, 0));
        yield return ("0x000000000100000000", new(0, 1, 0));
        yield return ("0x0000000001000001bc", new(0, 1, 444));
        yield return ("0x0000000001ffffffff", new(0, 1, UInt32.MaxValue));
        yield return ("0x00ffffffff00000000", new(0, UInt32.MaxValue, 0));
        yield return ("0x00ffffffff000001bc", new(0, UInt32.MaxValue, 444));
        yield return ("0x00ffffffffffffffff", new(0, UInt32.MaxValue, UInt32.MaxValue));

        // V1
        yield return ("0x0100000000000000000000000000000000", new(1, 0, 0, 0));
        yield return ("0x0100000001000000000000000000000001", new(1, 1, 0, 1));
        yield return ("0x0100000001000000010000000000000001", new(1, 1, 1, 1));
        yield return ("0x01000000010000000100000000000001bc", new(1, 1, 1, 444));
        yield return ("0x01000000010000000100000000ffffffff", new(1, 1, 1, UInt32.MaxValue));
        yield return ("0x010000000100000001ffffffffffffffff", new(1, 1, 1, UInt64.MaxValue));
        yield return ("0x01ffffffffffffffffffffffffffffffff", new(1, UInt32.MaxValue, UInt32.MaxValue, UInt64.MaxValue));
    }
    [TestCaseSource(nameof(DecodeBlockHeaderParametersCases))]
    public void DecodeBlockHeaderParameters((string HexString, EIP1559Parameters Expected) testCase)
    {
        var bytes = Bytes.FromHexString(testCase.HexString);
        var blockHeader = Build.A.BlockHeader.WithExtraData(bytes).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockHeader.TryDecodeEIP1559Parameters(out EIP1559Parameters decoded, out _), Is.True);
            Assert.That(decoded, Is.EqualTo(testCase.Expected));
        }
    }

    private static IEnumerable<(byte[], ulong?, EIP1559Parameters, string)> DecodePayloadAttributesParametersCases()
    {
        // V0: 8-byte eip1559Params, no MinBaseFee → 9-byte ExtraData
        yield return (new byte[] { 0, 0, 0, 1, 0, 0, 0, 0 }, null, new(0, 1, 0), "0x000000000100000000");
        yield return (new byte[] { 0, 0, 0, 1, 0, 0, 1, 0xbc }, null, new(0, 1, 444), "0x0000000001000001bc");

        // V1: 8-byte eip1559Params + MinBaseFee → 17-byte ExtraData
        yield return (new byte[] { 0, 0, 0, 1, 0, 0, 0, 0 }, 1UL, new(1, 1, 0, 1), "0x0100000001000000000000000000000001");
        yield return (new byte[] { 0, 0, 0, 1, 0, 0, 0, 1 }, 444UL, new(1, 1, 1, 444), "0x01000000010000000100000000000001bc");
    }

    [TestCaseSource(nameof(DecodePayloadAttributesParametersCases))]
    public void DecodePayloadAttributesAndProduceCorrectExtraData(
        (byte[] Eip1559Params, ulong? MinBaseFee, EIP1559Parameters Expected, string ExpectedExtraDataHex) testCase)
    {
        OptimismPayloadAttributes attributes = new()
        {
            GasLimit = 1,
            Transactions = [],
            EIP1559Params = testCase.Eip1559Params,
            MinBaseFee = testCase.MinBaseFee
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attributes.TryDecodeEIP1559Parameters(out EIP1559Parameters decoded, out _), Is.True);
            Assert.That(decoded, Is.EqualTo(testCase.Expected));

            byte[] extraData = new byte[decoded.ByteLength];
            decoded.WriteTo(extraData);
            Assert.That(extraData.ToHexString(true), Is.EqualTo(testCase.ExpectedExtraDataHex));
        }
    }
}
