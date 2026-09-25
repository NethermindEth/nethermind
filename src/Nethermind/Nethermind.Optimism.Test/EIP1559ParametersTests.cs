// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;
using System;
using Nethermind.Core;

namespace Nethermind.Optimism.Test;

[Parallelizable(ParallelScope.All)]
public class EIP1559ParametersTests
{
    private static IEnumerable<(string hexString, EIP1559Parameters expected)> DecodeBlockHeaderParametersCases()
    {
        // V0
        yield return ("0x000000000000000000", new(0, 0, 0));
        yield return ("0x0000000001000001bc", new(0, 1, 444));
        yield return ("0x0000000001ffffffff", new(0, 1, UInt32.MaxValue));
        yield return ("0x00ffffffff000001bc", new(0, UInt32.MaxValue, 444));
        yield return ("0x00ffffffffffffffff", new(0, UInt32.MaxValue, UInt32.MaxValue));

        // V1
        yield return ("0x0100000000000000000000000000000000", new(1, 0, 0, 0));
        yield return ("0x0100000001000000010000000000000001", new(1, 1, 1, 1));
        yield return ("0x01000000010000000100000000000001bc", new(1, 1, 1, 444));
        yield return ("0x01000000010000000100000000ffffffff", new(1, 1, 1, UInt32.MaxValue));
        yield return ("0x010000000100000001ffffffffffffffff", new(1, 1, 1, UInt64.MaxValue));
        yield return ("0x01ffffffffffffffffffffffffffffffff", new(1, UInt32.MaxValue, UInt32.MaxValue, UInt64.MaxValue));
    }
    [TestCaseSource(nameof(DecodeBlockHeaderParametersCases))]
    public void DecodeBlockHeaderParameters((string HexString, EIP1559Parameters Expected) testCase)
    {
        byte[] bytes = Bytes.FromHexString(testCase.HexString);
        BlockHeader blockHeader = Build.A.BlockHeader.WithExtraData(bytes).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockHeader.TryDecodeEIP1559Parameters(out EIP1559Parameters decoded, out _), Is.True);
            Assert.That(decoded, Is.EqualTo(testCase.Expected));
        }
    }

    // A zero elasticity would divide the next block's gas target by zero; op-geth rejects it unless the denominator is zero too.
    [TestCase("0x000000000100000000")]
    [TestCase("0x00ffffffff00000000")]
    [TestCase("0x00000000000000000a")]
    [TestCase("0x0100000001000000000000000000000001")]
    [TestCase("0x0100000000000000010000000000000000")]
    public void DecodeBlockHeaderParameters_RejectsOneSidedZero(string hexString)
    {
        BlockHeader blockHeader = Build.A.BlockHeader.WithExtraData(Bytes.FromHexString(hexString)).TestObject;

        Assert.That(blockHeader.TryDecodeEIP1559Parameters(out _, out _), Is.False);
    }
}
