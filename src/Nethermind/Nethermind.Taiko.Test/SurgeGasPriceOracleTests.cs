// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
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
        value.ToString("x").PadLeft(padding, '0');

    /// <summary>
    /// Creates a mock CoreState response.
    /// CoreState: nextProposalId (word 0), lastProposalBlockId (word 1), lastFinalizedProposalId (word 2), ...
    /// </summary>
    private static string CreateCoreStateResponse(ulong nextProposalId, ulong lastFinalizedProposalId)
    {
        return "0x" +
            CreatePaddedHex(nextProposalId) +           // word 0: nextProposalId
            CreatePaddedHex(0) +                        // word 1: lastProposalBlockId
            CreatePaddedHex(lastFinalizedProposalId) +  // word 2: lastFinalizedProposalId
            CreatePaddedHex(0) +                        // word 3: lastFinalizedTimestamp
            CreatePaddedHex(0) +                        // word 4: lastCheckpointTimestamp
            CreatePaddedHex(0);                         // word 5: lastFinalizedBlockHash
    }

    /// <summary>
    /// Creates a mock Config response with ringBufferSize at word 10.
    /// </summary>
    private static string CreateConfigResponse(ulong ringBufferSize)
    {
        // Config has 18 fields, ringBufferSize is at word 10
        string response = "0x";
        for (int i = 0; i < 18; i++)
        {
            response += i == 10 ? CreatePaddedHex(ringBufferSize) : CreatePaddedHex(0);
        }
        return response;
    }

    [SetUp]
    public void Setup()
    {
        _blockFinder = Substitute.For<IBlockFinder>();
        _logManager = LimboLogs.Instance;
        _specProvider = Substitute.For<ISpecProvider>();
        _l1RpcClient = Substitute.For<IJsonRpcClient>();
        _surgeConfig = new SurgeConfig
        {
            TaikoInboxAddress = "0x06a9Ab27c7e2255df1815E6CC0168d7755Feb19a",
            GasPriceRefreshTimeoutSeconds = 2,
            L2GasUsageWindowSize = 5
        };

        _gasPriceOracle = new SurgeGasPriceOracle(
            _blockFinder,
            _logManager,
            _specProvider,
            MinGasPrice,
            _l1RpcClient,
            _surgeConfig);
    }

    private void SetupBlockFinderWithBlocks(long headBlockNumber, long gasUsed = 1000000)
    {
        Block headBlock = Build.A.Block.WithNumber(headBlockNumber).WithGasUsed(gasUsed).TestObject;
        _blockFinder.Head.Returns(headBlock);

        for (long i = headBlockNumber; i >= Math.Max(0, headBlockNumber - _surgeConfig.L2GasUsageWindowSize + 1); i--)
        {
            _blockFinder.FindBlock(i, BlockTreeLookupOptions.RequireCanonical)
                .Returns(Build.A.Block.WithNumber(i).WithGasUsed(gasUsed).TestObject);
        }
    }

    private void SetupInboxContractMocks(ulong ringBufferSize = 100, ulong nextProposalId = 10, ulong lastFinalizedProposalId = 5)
    {
        // getConfig() selector without 0x prefix
        _l1RpcClient.Post<string>("eth_call", Arg.Is<object>(o =>
            o.ToString()!.ToLowerInvariant().Contains("c3f909d4")), "latest")
            .Returns(CreateConfigResponse(ringBufferSize));

        // getCoreState() selector without 0x prefix
        _l1RpcClient.Post<string>("eth_call", Arg.Is<object>(o =>
            o.ToString()!.ToLowerInvariant().Contains("6aa6a01a")), "latest")
            .Returns(CreateCoreStateResponse(nextProposalId, lastFinalizedProposalId));
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
        SetupBlockFinderWithBlocks(10);
        _l1RpcClient.Post<L1FeeHistoryResults?>("eth_feeHistory", _surgeConfig.FeeHistoryBlockCount, BlockParameter.Latest, null)
            .Returns(Task.FromResult<L1FeeHistoryResults?>(null));

        UInt256 gasPrice = await _gasPriceOracle.GetGasPriceEstimate();

        Assert.That(gasPrice, Is.EqualTo(MinGasPrice));
    }

    [Test]
    public async ValueTask GetGasPriceEstimate_WithEmptyFeeHistory_ReturnsMinGasPrice()
    {
        SetupBlockFinderWithBlocks(10);

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
        SetupInboxContractMocks();

        // Scenario 1: Low L1 base fee (10 Gwei average)
        SetupBlockFinderWithBlocks(10, 1000000);
        var lowFeeHistory = new L1FeeHistoryResults
        {
            BaseFeePerGas = [UInt256.Parse("10000000000")],
            BaseFeePerBlobGas = [UInt256.Parse("1000000000")]
        };

        _l1RpcClient.Post<L1FeeHistoryResults?>("eth_feeHistory", _surgeConfig.FeeHistoryBlockCount, BlockParameter.Latest, null)
            .Returns(Task.FromResult<L1FeeHistoryResults?>(lowFeeHistory));

        var oracleLowFee = new SurgeGasPriceOracle(
            _blockFinder, _logManager, _specProvider, MinGasPrice, _l1RpcClient, _surgeConfig);
        UInt256 gasPriceLowL1Fee = await oracleLowFee.GetGasPriceEstimate();

        // Scenario 2: High L1 base fee (50 Gwei average)
        SetupBlockFinderWithBlocks(20, 1000000);
        var highFeeHistory = new L1FeeHistoryResults
        {
            BaseFeePerGas = [UInt256.Parse("50000000000")],
            BaseFeePerBlobGas = [UInt256.Parse("1000000000")]
        };

        _l1RpcClient.Post<L1FeeHistoryResults?>("eth_feeHistory", _surgeConfig.FeeHistoryBlockCount, BlockParameter.Latest, null)
            .Returns(Task.FromResult<L1FeeHistoryResults?>(highFeeHistory));

        var oracleHighFee = new SurgeGasPriceOracle(
            _blockFinder, _logManager, _specProvider, MinGasPrice, _l1RpcClient, _surgeConfig);
        UInt256 gasPriceHighL1Fee = await oracleHighFee.GetGasPriceEstimate();

        // Higher L1 base fee should result in higher L2 gas price
        Assert.That(gasPriceHighL1Fee, Is.GreaterThan(gasPriceLowL1Fee));
    }

    [Test]
    public async ValueTask GetGasPriceEstimate_WithZeroGasUsed_UsesL2BlockGasTarget()
    {
        SetupBlockFinderWithBlocks(10, 0);
        SetupInboxContractMocks();

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

        Assert.That(gasPrice, Is.GreaterThan(UInt256.Zero));
    }

    [Test]
    public async ValueTask GetGasPriceEstimate_WithCachedPrice_ReturnsCachedPrice()
    {
        SetupBlockFinderWithBlocks(10);
        SetupInboxContractMocks();

        var feeHistory = new L1FeeHistoryResults
        {
            BaseFeePerGas = [UInt256.Parse("20000000000")],
            BaseFeePerBlobGas = [UInt256.Parse("1000000000")]
        };

        _l1RpcClient.Post<L1FeeHistoryResults?>("eth_feeHistory", _surgeConfig.FeeHistoryBlockCount, BlockParameter.Latest, null)
            .Returns(Task.FromResult<L1FeeHistoryResults?>(feeHistory));

        UInt256 firstGasPrice = await _gasPriceOracle.GetGasPriceEstimate();

        // Change the fee history to ensure we're using cache
        feeHistory.BaseFeePerGas[0] = UInt256.Parse("40000000000");

        UInt256 secondGasPrice = await _gasPriceOracle.GetGasPriceEstimate();

        Assert.That(secondGasPrice, Is.EqualTo(firstGasPrice));
    }

    [Test]
    public async ValueTask GetGasPriceEstimate_WithTimeout_RefreshesGasPrice()
    {
        SetupBlockFinderWithBlocks(10);
        SetupInboxContractMocks();

        var feeHistory = new L1FeeHistoryResults
        {
            BaseFeePerGas = [UInt256.Parse("20000000000")],
            BaseFeePerBlobGas = [UInt256.Parse("1000000000")]
        };

        _l1RpcClient.Post<L1FeeHistoryResults?>("eth_feeHistory", _surgeConfig.FeeHistoryBlockCount, BlockParameter.Latest, null)
            .Returns(Task.FromResult<L1FeeHistoryResults?>(feeHistory));

        UInt256 firstGasPrice = await _gasPriceOracle.GetGasPriceEstimate();

        await Task.Delay(_surgeConfig.GasPriceRefreshTimeoutSeconds * 1000 + 100);

        feeHistory.BaseFeePerGas[0] = UInt256.Parse("40000000000");

        UInt256 secondGasPrice = await _gasPriceOracle.GetGasPriceEstimate();

        Assert.That(secondGasPrice, Is.GreaterThan(firstGasPrice));
    }

    [Test]
    public async ValueTask GetGasPriceEstimate_WhenInboxBufferFull_UsesReducedProposalGas()
    {
        // Create two separate oracles with different inbox buffer states
        var feeHistory = new L1FeeHistoryResults
        {
            BaseFeePerGas = [UInt256.Parse("20000000000")],
            BaseFeePerBlobGas = [UInt256.Parse("1000000000")]
        };

        _l1RpcClient.Post<L1FeeHistoryResults?>("eth_feeHistory", _surgeConfig.FeeHistoryBlockCount, BlockParameter.Latest, null)
            .Returns(Task.FromResult<L1FeeHistoryResults?>(feeHistory));

        // First oracle: inbox buffer NOT full (uses FixedProposalGas = 75k)
        SetupBlockFinderWithBlocks(10);
        SetupInboxContractMocks(ringBufferSize: 100, nextProposalId: 50, lastFinalizedProposalId: 40);

        var oracleNotFull = new SurgeGasPriceOracle(
            _blockFinder, _logManager, _specProvider, MinGasPrice, _l1RpcClient, _surgeConfig);
        UInt256 gasPriceNotFull = await oracleNotFull.GetGasPriceEstimate();

        // Second oracle: inbox buffer IS full (uses FixedProposalGasWithFullInboxBuffer = 50k)
        SetupBlockFinderWithBlocks(11);
        SetupInboxContractMocks(ringBufferSize: 100, nextProposalId: 150, lastFinalizedProposalId: 50);

        var oracleFull = new SurgeGasPriceOracle(
            _blockFinder, _logManager, _specProvider, MinGasPrice, _l1RpcClient, _surgeConfig);
        UInt256 gasPriceFull = await oracleFull.GetGasPriceEstimate();

        // When buffer is full, lower proposal gas (50k vs 75k) should result in lower gas price
        Assert.That(gasPriceFull, Is.LessThan(gasPriceNotFull));
    }

    [Test]
    public async ValueTask GetGasPriceEstimate_ComputesAverageGasFromRecentBlocks()
    {
        var feeHistory = new L1FeeHistoryResults
        {
            BaseFeePerGas = [UInt256.Parse("20000000000")],
            BaseFeePerBlobGas = [UInt256.Parse("1000000000")]
        };

        _l1RpcClient.Post<L1FeeHistoryResults?>("eth_feeHistory", _surgeConfig.FeeHistoryBlockCount, BlockParameter.Latest, null)
            .Returns(Task.FromResult<L1FeeHistoryResults?>(feeHistory));

        SetupInboxContractMocks();

        // Scenario 1: Low gas usage blocks (average = 100k)
        const long headBlockNumber1 = 10;
        _blockFinder.Head.Returns(Build.A.Block.WithNumber(headBlockNumber1).WithGasUsed(100000).TestObject);
        for (int i = 0; i < _surgeConfig.L2GasUsageWindowSize; i++)
        {
            _blockFinder.FindBlock(headBlockNumber1 - i, BlockTreeLookupOptions.RequireCanonical)
                .Returns(Build.A.Block.WithNumber(headBlockNumber1 - i).WithGasUsed(100000).TestObject);
        }

        var oracleLowGas = new SurgeGasPriceOracle(
            _blockFinder, _logManager, _specProvider, MinGasPrice, _l1RpcClient, _surgeConfig);
        UInt256 gasPriceLowUsage = await oracleLowGas.GetGasPriceEstimate();

        // Scenario 2: High gas usage blocks (average = 500k)
        const long headBlockNumber2 = 20;
        _blockFinder.Head.Returns(Build.A.Block.WithNumber(headBlockNumber2).WithGasUsed(500000).TestObject);
        for (int i = 0; i < _surgeConfig.L2GasUsageWindowSize; i++)
        {
            _blockFinder.FindBlock(headBlockNumber2 - i, BlockTreeLookupOptions.RequireCanonical)
                .Returns(Build.A.Block.WithNumber(headBlockNumber2 - i).WithGasUsed(500000).TestObject);
        }

        var oracleHighGas = new SurgeGasPriceOracle(
            _blockFinder, _logManager, _specProvider, MinGasPrice, _l1RpcClient, _surgeConfig);
        UInt256 gasPriceHighUsage = await oracleHighGas.GetGasPriceEstimate();

        // Higher gas usage should result in lower gas price (cost spread over more gas)
        Assert.That(gasPriceHighUsage, Is.LessThan(gasPriceLowUsage));
    }
}
