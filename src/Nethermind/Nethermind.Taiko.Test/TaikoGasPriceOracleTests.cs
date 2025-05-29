// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Taiko.Rpc;
using NSubstitute;
using NUnit.Framework;
using System.Threading.Tasks;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc.Client;

namespace Nethermind.Taiko.Test;

public class TaikoGasPriceOracleTests
{
    private IBlockFinder _blockFinder = null!;
    private ILogManager _logManager = null!;
    private ISpecProvider _specProvider = null!;
    private IJsonRpcClient _l1RpcClient = null!;
    private TaikoGasPriceOracle _gasPriceOracle = null!;
    private static readonly UInt256 MinGasPrice = UInt256.Parse("1000000000"); // 1 Gwei

    [SetUp]
    public void Setup()
    {
        _blockFinder = Substitute.For<IBlockFinder>();
        _logManager = Substitute.For<ILogManager>();
        _specProvider = Substitute.For<ISpecProvider>();
        _l1RpcClient = Substitute.For<IJsonRpcClient>();

        _gasPriceOracle = new TaikoGasPriceOracle(
            _blockFinder,
            _logManager,
            _specProvider,
            MinGasPrice,
            _l1RpcClient);
    }

    [Test]
    public void GetGasPriceEstimate_WhenNoHeadBlock_ReturnsMinGasPrice()
    {
        _blockFinder.Head.Returns((Block)null!);

        UInt256 gasPrice = _gasPriceOracle.GetGasPriceEstimate();

        Assert.That(gasPrice, Is.EqualTo(MinGasPrice));
    }

    [Test]
    public void GetGasPriceEstimate_WhenL1FeeHistoryFails_ReturnsMinGasPrice()
    {
        Block headBlock = Build.A.Block.WithNumber(1).TestObject;
        _blockFinder.Head.Returns(headBlock);
        _l1RpcClient.Post<L1FeeHistoryResults?>(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<BlockParameter>(), Arg.Any<object>())
            .Returns(Task.FromResult<L1FeeHistoryResults?>(null));

        UInt256 gasPrice = _gasPriceOracle.GetGasPriceEstimate();

        Assert.That(gasPrice, Is.EqualTo(MinGasPrice));
    }

    [Test]
    public void GetGasPriceEstimate_WithEmptyFeeHistory_ReturnsMinGasPrice()
    {
        Block headBlock = Build.A.Block.WithNumber(1).TestObject;
        _blockFinder.Head.Returns(headBlock);

        var feeHistory = new L1FeeHistoryResults
        {
            BaseFeePerGas = [],
            BaseFeePerBlobGas = []
        };

        _l1RpcClient.Post<L1FeeHistoryResults?>(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<BlockParameter>(), Arg.Any<object>())
            .Returns(Task.FromResult<L1FeeHistoryResults?>(feeHistory));

        UInt256 gasPrice = _gasPriceOracle.GetGasPriceEstimate();

        Assert.That(gasPrice, Is.EqualTo(MinGasPrice));
    }

    [Test]
    public void GetGasPriceEstimate_WithValidL1FeeHistory_CalculatesCorrectGasPrice()
    {
        Block headBlock = Build.A.Block.WithNumber(1).TestObject;
        _blockFinder.Head.Returns(headBlock);

        // Dummy Ethereum L1 fee history
        var feeHistory = new L1FeeHistoryResults
        {
            BaseFeePerGas =
            [
                UInt256.Parse("15000000000"),
                UInt256.Parse("25000000000"),
                UInt256.Parse("35000000000")
            ],
            BaseFeePerBlobGas =
            [
                UInt256.Parse("1000000000"),
                UInt256.Parse("1500000000")
            ]
        };

        _l1RpcClient.Post<L1FeeHistoryResults?>(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<BlockParameter>(), Arg.Any<object>())
            .Returns(Task.FromResult<L1FeeHistoryResults?>(feeHistory));

        UInt256 gasPrice = _gasPriceOracle.GetGasPriceEstimate();

        Assert.That(gasPrice, Is.GreaterThan(MinGasPrice));
    }

    [Test]
    public void GetGasPriceEstimate_WithCachedPrice_ReturnsCachedPrice()
    {
        Block headBlock = Build.A.Block.WithNumber(1).TestObject;
        _blockFinder.Head.Returns(headBlock);

        var feeHistory = new L1FeeHistoryResults
        {
            BaseFeePerGas =
            [
                UInt256.Parse("20000000000")  // 20 Gwei
            ],
            BaseFeePerBlobGas =
            [
                UInt256.Parse("1000000000")   // 1 Gwei
            ]
        };

        _l1RpcClient.Post<L1FeeHistoryResults?>(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<BlockParameter>(), Arg.Any<object>())
            .Returns(Task.FromResult<L1FeeHistoryResults?>(feeHistory));

        UInt256 firstGasPrice = _gasPriceOracle.GetGasPriceEstimate();

        // Change the fee history to ensure we're using cache
        feeHistory.BaseFeePerGas[0] = UInt256.Parse("40000000000");

        // Second call should use cached value
        UInt256 secondGasPrice = _gasPriceOracle.GetGasPriceEstimate();

        Assert.That(secondGasPrice, Is.EqualTo(firstGasPrice));
    }
}
