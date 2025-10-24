// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;
using FluentAssertions;
using System;
using Nethermind.Optimism.ExtraParams;

namespace Nethermind.Optimism.Test;

[Parallelizable(ParallelScope.All)]
public class HoloceneExtraParamsTests
{
    private static IEnumerable<(string hexString, HoloceneExtraParams expected)> DecodeBlockHeaderParametersCases()
    {
        yield return ("0x000000000000000000", new HoloceneExtraParams { Denominator = 0, Elasticity = 0 });
        yield return ("0x000000000100000000", new HoloceneExtraParams { Denominator = 1, Elasticity = 0 });
        yield return ("0x0000000001000001bc", new HoloceneExtraParams { Denominator = 1, Elasticity = 444 });
        yield return ("0x0000000001ffffffff", new HoloceneExtraParams { Denominator = 1, Elasticity = UInt32.MaxValue });
        yield return ("0x00ffffffff00000000", new HoloceneExtraParams { Denominator = UInt32.MaxValue, Elasticity = 0 });
        yield return ("0x00ffffffff000001bc", new HoloceneExtraParams { Denominator = UInt32.MaxValue, Elasticity = 444 });
        yield return ("0x00ffffffffffffffff", new HoloceneExtraParams { Denominator = UInt32.MaxValue, Elasticity = UInt32.MaxValue });
    }
    [TestCaseSource(nameof(DecodeBlockHeaderParametersCases))]
    public void DecodeBlockHeaderParameters((string HexString, HoloceneExtraParams Expected) testCase)
    {
        var bytes = Bytes.FromHexString(testCase.HexString);
        var blockHeader = Build.A.BlockHeader.WithExtraData(bytes).TestObject;

        HoloceneExtraParams.TryParse(blockHeader, out HoloceneExtraParams decoded, out _).Should().BeTrue();
        decoded.Should().Be(testCase.Expected);
    }
}
