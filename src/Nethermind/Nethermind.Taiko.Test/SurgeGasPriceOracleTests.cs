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

    private static string CreatePaddedHex(UInt256 value, int padding = 64) =>
        value.ToString("x64").PadLeft(padding, '0');

    [SetUp]
    public void Setup()
    {
        _blockFinder = Substitute.For<IBlockFinder>();
        _logManager = LimboLogs.Instance;
        _specProvider = Substitute.For<ISpecProvider>();
        _l1RpcClient = Substitute.For<IJsonRpcClient>();
        _surgeConfig = new SurgeConfig
        {
            TaikoInboxAddress = "0x06a9Ab27c7e2255df1815E6CC0168d7755Feb19a"
        };

        _gasPriceOracle = new SurgeGasPriceOracle(
            _blockFinder,
            _logManager,
            _specProvider,
            MinGasPrice,
            _l1RpcClient,
            _surgeConfig);
    }

    [Test]
    public async ValueTask GetGasPriceEstimate_WhenNoHeadBlock_ReturnsMinGasPrice()
    {
        _blockFinder.Head.Returns((Block)null!);

        UInt256 gasPrice = await _gasPriceOracle.GetGasPriceEstimate();

        Assert.That(gasPrice, Is.EqualTo(MinGasPrice));
    }

    [Test]
    public async ValueTask GetGasPriceEstimate_WhenL1FeeHistoryFails_ReturnsMinGasPrice()
    {
        Block headBlock = Build.A.Block.WithNumber(1).WithGasUsed(1000000).TestObject;
        _blockFinder.Head.Returns(headBlock);
        _l1RpcClient.Post<L1FeeHistoryResults?>("eth_feeHistory", _surgeConfig.FeeHistoryBlockCount, BlockParameter.Latest, null)
            .Returns(Task.FromResult<L1FeeHistoryResults?>(null));

        UInt256 gasPrice = await _gasPriceOracle.GetGasPriceEstimate();

        Assert.That(gasPrice, Is.EqualTo(MinGasPrice));
    }

    [Test]
    public async ValueTask GetGasPriceEstimate_WithEmptyFeeHistory_ReturnsMinGasPrice()
    {
        Block headBlock = Build.A.Block.WithNumber(1).WithGasUsed(1000000).TestObject;
        _blockFinder.Head.Returns(headBlock);

        var feeHistory = new L1FeeHistoryResults
        {
            BaseFeePerGas = [],
            BaseFeePerBlobGas = []
        };

        _l1RpcClient.Post<L1FeeHistoryResults?>("eth_feeHistory", _surgeConfig.FeeHistoryBlockCount, BlockParameter.Latest, null)
            .Returns(Task.FromResult<L1FeeHistoryResults?>(feeHistory));

        UInt256 gasPrice = await _gasPriceOracle.GetGasPriceEstimate();

        Assert.That(gasPrice, Is.EqualTo(MinGasPrice));
    }

    [Test]
    public async ValueTask GetGasPriceEstimate_WithValidL1FeeHistory_CalculatesCorrectGasPrice()
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
        _l1RpcClient.Post<L1FeeHistoryResults?>("eth_feeHistory", _surgeConfig.FeeHistoryBlockCount, BlockParameter.Latest, null)
            .Returns(Task.FromResult<L1FeeHistoryResults?>(feeHistory));

        // Mock Stats2 returned by getStats2() call to have 2 batches (numBatches=2)
        var stats2Response = "0x" + CreatePaddedHex(2) + CreatePaddedHex(0, 192);
        _l1RpcClient.Post<string>("eth_call", Arg.Is<object>(o =>
            o.ToString()!.ToLowerInvariant().Contains("0x26baca1c")), "latest")
            .Returns(stats2Response);

        // Mock Batch returned by getBatch(1) call to have lastBlockId=1
        var batchResponse = "0x" + CreatePaddedHex(0) + CreatePaddedHex(1) + CreatePaddedHex(0, 576);
        _l1RpcClient.Post<string>("eth_call", Arg.Is<object>(o =>
            o.ToString()!.ToLowerInvariant().Contains("0x888775d9")), "latest")
            .Returns(batchResponse);

        // Mock block finder to return block with gas usage
        _blockFinder.FindBlock(1, Arg.Any<Blockchain.BlockTreeLookupOptions>())
            .Returns(Build.A.Block.WithNumber(1).WithGasUsed(1000000).TestObject);

        UInt256 gasPrice = await _gasPriceOracle.GetGasPriceEstimate();

        Assert.That(gasPrice, Is.GreaterThan(MinGasPrice));
    }

    [Test]
    public async ValueTask GetGasPriceEstimate_WithZeroGasUsed_ReturnsMinGasPrice()
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

        _l1RpcClient.Post<L1FeeHistoryResults?>("eth_feeHistory", _surgeConfig.FeeHistoryBlockCount, BlockParameter.Latest, null)
            .Returns(Task.FromResult<L1FeeHistoryResults?>(feeHistory));

        UInt256 gasPrice = await _gasPriceOracle.GetGasPriceEstimate();

        Assert.That(gasPrice, Is.EqualTo(MinGasPrice));
    }

    [Test]
    public async ValueTask GetGasPriceEstimate_WithCachedPrice_ReturnsCachedPrice()
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

        _l1RpcClient.Post<L1FeeHistoryResults?>("eth_feeHistory", _surgeConfig.FeeHistoryBlockCount, BlockParameter.Latest, null)
            .Returns(Task.FromResult<L1FeeHistoryResults?>(feeHistory));

        UInt256 firstGasPrice = await _gasPriceOracle.GetGasPriceEstimate();

        // Change the fee history to ensure we're using cache
        feeHistory.BaseFeePerGas[0] = UInt256.Parse("40000000000");

        // Second call should use cached value
        UInt256 secondGasPrice = await _gasPriceOracle.GetGasPriceEstimate();

        Assert.That(secondGasPrice, Is.EqualTo(firstGasPrice));
    }

    [Test]
    [Explicit("This test requires interacting with a live TaikoInbox contract")]
    public async ValueTask GetGasPriceEstimate_WithLiveTaikoInboxContract_ReturnsValidGasPrice()
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
        _surgeConfig.TaikoInboxAddress = "0x06a9Ab27c7e2255df1815E6CC0168d7755Feb19a";
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
        UInt256 gasPrice = await liveGasPriceOracle.GetGasPriceEstimate();

        // Verify the gas price is valid
        Assert.That(gasPrice, Is.GreaterThan(MinGasPrice));
        Assert.That(gasPrice, Is.LessThan(UInt256.Parse("100000000000"))); // Less than 100 Gwei
    }
}
