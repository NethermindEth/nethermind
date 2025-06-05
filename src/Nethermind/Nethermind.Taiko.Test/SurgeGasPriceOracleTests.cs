// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Taiko.Config;
using Nethermind.Taiko.Rpc;

namespace Nethermind.Taiko.Test;

public class SurgeGasPriceOracleTests
{
    private IBlockFinder _blockFinder = null!;
    private ILogManager _logManager = null!;
    private ISpecProvider _specProvider = null!;
    private IJsonRpcClient _l1RpcClient = null!;
    private ISurgeConfig _surgeConfig = null!;
    private SurgeGasPriceOracle _gasPriceOracle = null!;
    private static readonly UInt256 MinGasPrice = UInt256.Parse("1000000000"); // 1 Gwei

    [SetUp]
    public void Setup()
    {
        _blockFinder = Substitute.For<IBlockFinder>();
        _logManager = LimboLogs.Instance;
        _specProvider = Substitute.For<ISpecProvider>();
        _l1RpcClient = Substitute.For<IJsonRpcClient>();
        _surgeConfig = new SurgeConfig();

        _gasPriceOracle = new SurgeGasPriceOracle(
            _blockFinder,
            _logManager,
            _specProvider,
            MinGasPrice,
            _l1RpcClient,
            _surgeConfig);
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
        Block headBlock = Build.A.Block.WithNumber(1).WithGasUsed(1000000).TestObject;
        _blockFinder.Head.Returns(headBlock);
        _l1RpcClient.Post<L1FeeHistoryResults?>(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<BlockParameter>(), Arg.Any<object>())
            .Returns(Task.FromResult<L1FeeHistoryResults?>(null));

        UInt256 gasPrice = _gasPriceOracle.GetGasPriceEstimate();

        Assert.That(gasPrice, Is.EqualTo(MinGasPrice));
    }

    [Test]
    public void GetGasPriceEstimate_WithEmptyFeeHistory_ReturnsMinGasPrice()
    {
        Block headBlock = Build.A.Block.WithNumber(1).WithGasUsed(1000000).TestObject;
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
        Block headBlock = Build.A.Block.WithNumber(1).WithGasUsed(1000000).TestObject;
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

        // Set up the mock to match the exact parameters that will be passed
        _l1RpcClient.Post<L1FeeHistoryResults?>("eth_feeHistory", 200, BlockParameter.Latest, null)
            .Returns(Task.FromResult<L1FeeHistoryResults?>(feeHistory));

        UInt256 gasPrice = _gasPriceOracle.GetGasPriceEstimate();

        Assert.That(gasPrice, Is.GreaterThan(MinGasPrice));
    }

    [Test]
    public void GetGasPriceEstimate_WithZeroGasUsed_ReturnsMinGasPrice()
    {
        Block headBlock = Build.A.Block.WithNumber(1).WithGasUsed(0).TestObject;
        _blockFinder.Head.Returns(headBlock);

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

        _l1RpcClient.Post<L1FeeHistoryResults?>("eth_feeHistory", 200, BlockParameter.Latest, null)
            .Returns(Task.FromResult<L1FeeHistoryResults?>(feeHistory));

        UInt256 gasPrice = _gasPriceOracle.GetGasPriceEstimate();

        Assert.That(gasPrice, Is.EqualTo(MinGasPrice));
    }

    [Test]
    public void GetGasPriceEstimate_WithCachedPrice_ReturnsCachedPrice()
    {
        Block headBlock = Build.A.Block.WithNumber(1).WithGasUsed(1000000).TestObject;
        _blockFinder.Head.Returns(headBlock);

        var feeHistory = new L1FeeHistoryResults
        {
            BaseFeePerGas =
            [
                UInt256.Parse("20000000000")
            ],
            BaseFeePerBlobGas =
            [
                UInt256.Parse("1000000000")
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

    [Test]
    public void GetGasPriceEstimate_WithLiveTaikoInboxContract_ReturnsValidGasPrice()
    {
        // Create a real RPC client for L1
        var l1RpcClient = new BasicJsonRpcClient(
            new Uri("https://eth.llamarpc.com"),
            new EthereumJsonSerializer(),
            _logManager);

        // Set up the block finder to return a valid block with gas usage for any block ID
        _blockFinder.FindBlock(Arg.Any<long>(), Arg.Any<Blockchain.BlockTreeLookupOptions>())
            .Returns(callInfo => Build.A.Block
                .WithNumber(callInfo.Arg<long>())
                .WithGasUsed(1000000)
                .TestObject);

        // Create a gas price oracle with the live client
        var liveGasPriceOracle = new SurgeGasPriceOracle(
            _blockFinder,
            _logManager,
            _specProvider,
            MinGasPrice,
            l1RpcClient,
            _surgeConfig);

        // Set up a head block with some gas used
        Block headBlock = Build.A.Block.WithNumber(1).WithGasUsed(1000000).TestObject;
        _blockFinder.Head.Returns(headBlock);

        // Get the gas price estimate
        UInt256 gasPrice = liveGasPriceOracle.GetGasPriceEstimate();

        // Verify the gas price is valid
        Assert.That(gasPrice, Is.GreaterThan(MinGasPrice));
        Assert.That(gasPrice, Is.LessThan(UInt256.Parse("100000000000"))); // Less than 100 Gwei
    }
}
