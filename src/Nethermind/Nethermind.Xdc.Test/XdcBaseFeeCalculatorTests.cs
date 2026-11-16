// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Xdc.Spec;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
public class XdcBaseFeeCalculatorTests
{
    private const ulong Gas50xBlock = 1000;
    private const ulong Gas2500xBlock = 2000;
    private const long Gas2500xBaseFee = 625_000_000_000;

    private readonly XdcBaseFeeCalculator _calculator =
        new(new XdcChainSpecEngineParameters { Gas50xBlock = Gas50xBlock, Gas2500xBlock = Gas2500xBlock });

    [TestCase(true, Gas50xBlock - 1, XdcBaseFeeCalculator.BaseFee, TestName = "Before any tier")]
    [TestCase(true, Gas50xBlock, XdcBaseFeeCalculator.BaseFee, TestName = "Gas50x tier")]
    [TestCase(true, Gas2500xBlock - 1, XdcBaseFeeCalculator.BaseFee, TestName = "Last Gas50x block")]
    [TestCase(true, Gas2500xBlock, Gas2500xBaseFee, TestName = "Gas2500x tier")]
    [TestCase(false, Gas2500xBlock, 0L, TestName = "EIP-1559 disabled")]
    public void Calculate_ReturnsTierBaseFeeOfChildBlock(bool isEip1559Enabled, ulong childNumber, long expectedBaseFee)
    {
        IReleaseSpec releaseSpec = ReleaseSpecSubstitute.Create();
        releaseSpec.IsEip1559Enabled.Returns(isEip1559Enabled);

        BlockHeader parent = Build.A.BlockHeader
            .WithNumber(childNumber - 1)
            .WithBaseFee(1_000_000)
            .WithGasUsed(15_000_000)
            .WithGasLimit(30_000_000)
            .TestObject;

        UInt256 actualBaseFee = _calculator.Calculate(parent, releaseSpec);

        Assert.That(actualBaseFee, Is.EqualTo((UInt256)expectedBaseFee));
    }
}
