// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
public class XdcBaseFeeCalculatorTests
{
    private readonly XdcBaseFeeCalculator _calculator = new();

    [TestCase(true, XdcBaseFeeCalculator.BaseFee)]
    [TestCase(false, 0L)]
    public void Calculate_ReturnsConstantBaseFeeOnlyWhenEip1559Enabled(bool isEip1559Enabled, long expectedBaseFee)
    {
        IReleaseSpec releaseSpec = ReleaseSpecSubstitute.Create();
        releaseSpec.IsEip1559Enabled.Returns(isEip1559Enabled);

        BlockHeader parent = Build.A.BlockHeader
            .WithBaseFee(1_000_000)
            .WithGasUsed(15_000_000)
            .WithGasLimit(30_000_000)
            .TestObject;

        UInt256 actualBaseFee = _calculator.Calculate(parent, releaseSpec);

        Assert.That(actualBaseFee, Is.EqualTo((UInt256)expectedBaseFee));
    }
}
