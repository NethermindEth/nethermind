// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;
using FluentAssertions;
using System;

namespace Nethermind.Optimism.Test;

[Parallelizable(ParallelScope.All)]
public class EIP1559ParametersTests
{
    private static IEnumerable<(string hexString, EIP1559Parameters expected)> DecodeBlockHeaderParametersCases()
    {
        yield return ("0x000000000000000000", new(0, 0, 0));
        yield return ("0x000000000100000000", new(0, 1, 0));
        yield return ("0x0000000001000001bc", new(0, 1, 444));
        yield return ("0x0000000001ffffffff", new(0, 1, UInt32.MaxValue));
        yield return ("0x00ffffffff00000000", new(0, UInt32.MaxValue, 0));
        yield return ("0x00ffffffff000001bc", new(0, UInt32.MaxValue, 444));
        yield return ("0x00ffffffffffffffff", new(0, UInt32.MaxValue, UInt32.MaxValue));
    }
    [TestCaseSource(nameof(DecodeBlockHeaderParametersCases))]
    public void DecodeBlockHeaderParameters((string HexString, EIP1559Parameters Expected) testCase)
    {
        var bytes = Bytes.FromHexString(testCase.HexString);
        var blockHeader = Build.A.BlockHeader.WithExtraData(bytes).TestObject;

        blockHeader.TryDecodeEIP1559Parameters(out EIP1559Parameters decoded, out _);

        decoded.Should().Be(testCase.Expected);
    }
}
