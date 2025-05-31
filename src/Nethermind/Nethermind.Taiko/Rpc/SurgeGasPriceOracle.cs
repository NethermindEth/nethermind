// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.Taiko.Config;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.Taiko.Rpc;

public class SurgeGasPriceOracle : GasPriceOracle
{
    private readonly IJsonRpcClient _l1RpcClient;
    private readonly ISurgeConfig _surgeConfig;

    private const int BlobSize = 128 * 1024;

    public SurgeGasPriceOracle(
        IBlockFinder blockFinder,
        ILogManager logManager,
        ISpecProvider specProvider,
        UInt256 minGasPrice,
        IJsonRpcClient l1RpcClient,
        ISurgeConfig surgeConfig) : base(blockFinder, specProvider, logManager, minGasPrice)
    {
        _l1RpcClient = l1RpcClient;
        _surgeConfig = surgeConfig;
    }

    private UInt256 FallbackGasPrice() => _gasPriceEstimation.LastPrice ?? _minGasPrice;

    public override UInt256 GetGasPriceEstimate()
    {
        Block? headBlock = _blockFinder.Head;
        if (headBlock is null)
        {
            if (_logger.IsTrace) _logger.Trace("[SurgeGasPriceOracle] No head block available, using fallback gas price");
            return FallbackGasPrice();
        }

        Hash256 headBlockHash = headBlock.Hash!;
        if (_gasPriceEstimation.TryGetPrice(headBlockHash, out UInt256? price))
        {
            if (_logger.IsTrace) _logger.Trace($"[SurgeGasPriceOracle] Using cached gas price estimate: {price}");
            return price!.Value;
        }

        // Get the fee history from the L1 client with RPC
        var feeHistory = GetL1FeeHistory().GetAwaiter().GetResult();
        if (feeHistory == null || feeHistory.BaseFeePerGas.Length == 0)
        {
            if (_logger.IsTrace) _logger.Trace("[SurgeGasPriceOracle] Failed to get fee history, using fallback gas price");
            return FallbackGasPrice();
        }

        // Get the latest base fee and blob base fee from the fee history
        UInt256 l1BaseFee = feeHistory.BaseFeePerGas[^1];
        UInt256 l1BlobBaseFee = feeHistory.BaseFeePerBlobGas.Length > 0 ? feeHistory.BaseFeePerBlobGas[^1] : UInt256.Zero;
        UInt256 l1AverageBaseFee = (UInt256)feeHistory.BaseFeePerGas.Average(fee => (decimal)fee);

        // Compute the gas cost to post a batch on L1
        UInt256 costWithCallData = _surgeConfig.BatchPostingGasWithCallData * l1BaseFee;
        UInt256 costWithBlobs = (_surgeConfig.BatchPostingGasWithoutCallData * l1BaseFee) + (BlobSize * l1BlobBaseFee);
        UInt256 minProposingCost = UInt256.Min(costWithCallData, costWithBlobs);

        UInt256 proofPostingCost = _surgeConfig.ProofPostingGas * UInt256.Max(l1BaseFee, l1AverageBaseFee);

        UInt256 gasPriceEstimate = (minProposingCost + proofPostingCost + _surgeConfig.ProvingCostPerL2Batch) / _surgeConfig.L2GasPerL2Batch;
        _gasPriceEstimation.Set(headBlockHash, gasPriceEstimate);

        if (_logger.IsTrace) _logger.Trace($"[SurgeGasPriceOracle] Calculated new gas price estimate: {gasPriceEstimate}, " +
            $"L1 Base Fee: {l1BaseFee}, L1 Blob Base Fee: {l1BlobBaseFee}, L1 Average Base Fee: {l1AverageBaseFee}");

        return gasPriceEstimate;
    }

    private async Task<L1FeeHistoryResults?> GetL1FeeHistory()
    {
        try
        {
            return await _l1RpcClient.Post<L1FeeHistoryResults?>("eth_feeHistory", _surgeConfig.FeeHistoryBlockCount, BlockParameter.Latest, null);
        }
        catch (Exception ex)
        {
            if (_logger.IsTrace) _logger.Trace($"[SurgeGasPriceOracle] Failed to get fee history: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// L1 fee history results with arrays instead of ArrayPoolList for JSON deserialization.
/// </summary>
public class L1FeeHistoryResults
{
    public UInt256[] BaseFeePerGas { get; set; } = [];
    public UInt256[] BaseFeePerBlobGas { get; set; } = [];
    public double[] GasUsedRatio { get; set; } = [];
    public double[] BlobGasUsedRatio { get; set; } = [];
    public long OldestBlock { get; set; }
    public UInt256[][]? Reward { get; set; }
}
