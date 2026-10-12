// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
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

    private static (ISpecProvider SpecProvider, IChainHeadInfoProvider ChainHead) Context(
        bool eip1559Enabled, UInt256? minimumGasPrice = null)
    {
        IXdcReleaseSpec xdcSpec = Substitute.For<IXdcReleaseSpec>();
        xdcSpec.MinimumGasPrice.Returns(minimumGasPrice ?? MinGasPrice);
        xdcSpec.IsEip1559Enabled.Returns(eip1559Enabled);

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        IChainHeadInfoProvider chainHead = Substitute.For<IChainHeadInfoProvider>();
        chainHead.HeadNumber.Returns(HeadNumber);

        return (specProvider, chainHead);
    }

    /// <summary>Wraps a substitute oracle, to pin the decorator's own contract independently of the default oracle.</summary>
    private static XdcGasPriceOracle WrapEstimates(bool eip1559Enabled, UInt256 gasPrice, UInt256 priorityFee,
        UInt256? minimumGasPrice = null)
    {
        IGasPriceOracle inner = Substitute.For<IGasPriceOracle>();
        inner.GetGasPriceEstimate().Returns(ValueTask.FromResult(gasPrice));
        inner.GetMaxPriorityGasFeeEstimate().Returns(priorityFee);

        (ISpecProvider specProvider, IChainHeadInfoProvider chainHead) = Context(eip1559Enabled, minimumGasPrice);
        return new XdcGasPriceOracle(inner, specProvider, chainHead);
    }

    /// <summary>Wraps the real oracle on an empty chain, where the estimate falls back to its configured floor.</summary>
    private static XdcGasPriceOracle WrapDefaultOracleOnEmptyChain(bool eip1559Enabled, UInt256 blocksConfigMinGasPrice)
    {
        (ISpecProvider specProvider, IChainHeadInfoProvider chainHead) = Context(eip1559Enabled);

        Block head = Build.A.Block.WithNumber(HeadNumber)
            .WithBaseFeePerGas(eip1559Enabled ? MinGasPrice : UInt256.Zero)
            .TestObject;
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.Head.Returns(head);

        GasPriceOracle inner = new(blockFinder, specProvider, LimboLogs.Instance, blocksConfigMinGasPrice);
        return new XdcGasPriceOracle(inner, specProvider, chainHead);
    }

    [TestCase(0ul, TestName = "Blocks.MinGasPrice of zero, as the shipped XDC configs set it")]
    [TestCase(1ul, TestName = "Blocks.MinGasPrice of one wei, the Nethermind default")]
    public async Task GetGasPriceEstimate_BeforeEip1559_IsAtLeastTheMinimumThePoolEnforces(ulong blocksConfigMinGasPrice)
    {
        XdcGasPriceOracle oracle = WrapDefaultOracleOnEmptyChain(eip1559Enabled: false, blocksConfigMinGasPrice);

        Assert.That(await oracle.GetGasPriceEstimate(), Is.GreaterThanOrEqualTo((UInt256)MinGasPrice));
    }

    [Test]
    public async Task GetGasPriceEstimate_AfterEip1559_KeepsTheBaseFeeMargin()
    {
        XdcGasPriceOracle oracle = WrapDefaultOracleOnEmptyChain(eip1559Enabled: true, UInt256.Zero);

        // (minGasPrice + baseFee) * 110% with a zero Blocks.MinGasPrice, i.e. the margin over the base fee rather
        // than the doubled floor that handing the minimum to the default oracle would produce.
        Assert.That(await oracle.GetGasPriceEstimate(), Is.EqualTo((UInt256)(MinGasPrice * 110 / 100)));
    }

    [TestCase(true, TestName = "After EIP-1559")]
    [TestCase(false, TestName = "Before EIP-1559")]
    public async Task GetGasPriceEstimate_DoesNotLowerAnEstimateThatAlreadyClearsTheFloor(bool eip1559Enabled)
    {
        UInt256 sampled = MinGasPrice * 4;
        XdcGasPriceOracle oracle = WrapEstimates(eip1559Enabled, sampled, priorityFee: UInt256.Zero);

        Assert.That(await oracle.GetGasPriceEstimate(), Is.EqualTo(sampled));
    }

    [Test]
    public async Task GetGasPriceEstimate_AfterEip1559_IsStillClampedWhenTheEstimateFallsBelowTheFloor()
    {
        // The pool compares MaxFeePerGas whether or not EIP-1559 is live, so the suggestion has to clear the floor
        // in both regimes - unlike the priority fee, which the base fee already covers.
        XdcGasPriceOracle oracle = WrapEstimates(eip1559Enabled: true, gasPrice: 1, priorityFee: UInt256.Zero);

        Assert.That(await oracle.GetGasPriceEstimate(), Is.EqualTo((UInt256)MinGasPrice));
    }

    [Test]
    public async Task GetGasPriceEstimate_ZeroFloor_IsNotRaised()
    {
        XdcGasPriceOracle oracle = WrapEstimates(eip1559Enabled: false, gasPrice: 1, priorityFee: 1,
            minimumGasPrice: UInt256.Zero);

        Assert.That(await oracle.GetGasPriceEstimate(), Is.EqualTo(UInt256.One));
    }

    [Test]
    public void GetMaxPriorityGasFeeEstimate_BeforeEip1559_IsAtLeastTheMinimumThePoolEnforces()
    {
        XdcGasPriceOracle oracle = WrapEstimates(eip1559Enabled: false, gasPrice: UInt256.Zero, priorityFee: 1);

        Assert.That(oracle.GetMaxPriorityGasFeeEstimate(), Is.EqualTo((UInt256)MinGasPrice));
    }

    [Test]
    public void GetMaxPriorityGasFeeEstimate_AfterEip1559_IsNotRaisedToTheMinimum()
    {
        // The base fee already covers the floor; raising the tip too would ask for the minimum twice over.
        XdcGasPriceOracle oracle = WrapEstimates(eip1559Enabled: true, gasPrice: UInt256.Zero, priorityFee: 1);

        Assert.That(oracle.GetMaxPriorityGasFeeEstimate(), Is.EqualTo(UInt256.One));
    }
}
