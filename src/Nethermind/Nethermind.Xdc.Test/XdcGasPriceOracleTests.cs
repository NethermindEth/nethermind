// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Xdc.RPC;
using Nethermind.Xdc.Spec;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[Parallelizable(ParallelScope.All)]
internal class XdcGasPriceOracleTests
{
    private const ulong MinGasPrice = XdcConstants.DefaultMinGasPrice * XdcConstants.Gas50xMultiplier;
    private const ulong HeadNumber = 100;

    private static XdcGasPriceOracle CreateOracle(
        bool eip1559Enabled,
        UInt256? minimumGasPrice = null,
        UInt256? blocksConfigMinGasPrice = null)
    {
        IXdcReleaseSpec xdcSpec = Substitute.For<IXdcReleaseSpec>();
        xdcSpec.MinimumGasPrice.Returns(minimumGasPrice ?? MinGasPrice);
        xdcSpec.IsEip1559Enabled.Returns(eip1559Enabled);

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        // No block is resolvable behind the head, so there are no transactions to sample and the oracle
        // falls back to its configured floor - the case where the suggestion has to stand on its own.
        Block head = Build.A.Block.WithNumber(HeadNumber)
            .WithBaseFeePerGas(eip1559Enabled ? MinGasPrice : UInt256.Zero)
            .TestObject;
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.Head.Returns(head);

        IChainHeadInfoProvider chainHeadInfoProvider = Substitute.For<IChainHeadInfoProvider>();
        chainHeadInfoProvider.HeadNumber.Returns(HeadNumber);

        return new XdcGasPriceOracle(blockFinder, specProvider, chainHeadInfoProvider, LimboLogs.Instance,
            blocksConfigMinGasPrice ?? UInt256.Zero);
    }

    [TestCase(0ul, TestName = "Blocks.MinGasPrice of zero, as the shipped XDC configs set it")]
    [TestCase(1ul, TestName = "Blocks.MinGasPrice of one wei, the Nethermind default")]
    public async Task GetGasPriceEstimate_BeforeEip1559_IsAtLeastTheMinimumThePoolEnforces(ulong blocksConfigMinGasPrice)
    {
        XdcGasPriceOracle oracle = CreateOracle(eip1559Enabled: false, blocksConfigMinGasPrice: blocksConfigMinGasPrice);

        UInt256 estimate = await oracle.GetGasPriceEstimate();

        Assert.That(estimate, Is.GreaterThanOrEqualTo((UInt256)MinGasPrice));
    }

    [Test]
    public async Task GetGasPriceEstimate_AfterEip1559_KeepsTheBaseFeeMargin()
    {
        XdcGasPriceOracle oracle = CreateOracle(eip1559Enabled: true);

        UInt256 estimate = await oracle.GetGasPriceEstimate();

        // (minGasPrice + baseFee) * 110% with a zero Blocks.MinGasPrice, i.e. the margin over the base fee
        // rather than the doubled floor that feeding the minimum to the base constructor would produce.
        Assert.That(estimate, Is.EqualTo((UInt256)(MinGasPrice * 110 / 100)));
    }

    [Test]
    public async Task GetGasPriceEstimate_ZeroFloor_IsNotRaised()
    {
        XdcGasPriceOracle oracle = CreateOracle(eip1559Enabled: false, minimumGasPrice: UInt256.Zero);

        Assert.That(await oracle.GetGasPriceEstimate(), Is.EqualTo(UInt256.Zero));
    }

    [Test]
    public void GetMaxPriorityGasFeeEstimate_BeforeEip1559_IsAtLeastTheMinimumThePoolEnforces()
    {
        XdcGasPriceOracle oracle = CreateOracle(eip1559Enabled: false);

        Assert.That(oracle.GetMaxPriorityGasFeeEstimate(), Is.GreaterThanOrEqualTo((UInt256)MinGasPrice));
    }

    [Test]
    public void GetMaxPriorityGasFeeEstimate_AfterEip1559_IsNotRaisedToTheMinimum()
    {
        XdcGasPriceOracle oracle = CreateOracle(eip1559Enabled: true);

        // The base fee already covers the floor; raising the tip too would ask for the minimum twice over.
        Assert.That(oracle.GetMaxPriorityGasFeeEstimate(), Is.LessThan((UInt256)MinGasPrice));
    }
}
