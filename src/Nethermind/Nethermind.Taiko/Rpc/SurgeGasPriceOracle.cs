// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.Taiko.Config;

namespace Nethermind.Taiko.Rpc;

public class SurgeGasPriceOracle : GasPriceOracle
{
    private const string ClassName = nameof(SurgeGasPriceOracle);
    private const int BlobGasPerBlob = 131072;

    // ABI signatures and encoded function selectors for TaikoInbox contract.
    private static readonly AbiSignature GetCoreStateSignature = new("getCoreState");
    private static readonly AbiSignature GetConfigSignature = new("getConfig");
    private static readonly string GetCoreStateHexData = "0x" + Convert.ToHexString(
        AbiEncoder.Instance.Encode(AbiEncodingStyle.IncludeSignature, GetCoreStateSignature));
    private static readonly string GetConfigHexData = "0x" + Convert.ToHexString(
        AbiEncoder.Instance.Encode(AbiEncodingStyle.IncludeSignature, GetConfigSignature));

    private readonly IJsonRpcClient _l1RpcClient;
    private readonly ISurgeConfig _surgeConfig;

    private DateTime _lastGasPriceCalculation = DateTime.MinValue;
    private ulong _averageGasUsage;
    private ulong _inboxRingBufferSize;
    private bool _isInboxRingBufferFull;

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

    public override async ValueTask<UInt256> GetGasPriceEstimate()
    {
        Block? headBlock = _blockFinder.Head;
        if (headBlock is null)
        {
            if (_logger.IsTrace) _logger.Trace($"[{ClassName}] No head block available, using fallback gas price");
            return FallbackGasPrice();
        }

        Hash256 headBlockHash = headBlock.Hash!;
        bool forceRefresh = ForceRefreshGasPrice();

        // Check if the cached price exists.
        if (_gasPriceEstimation.TryGetPrice(headBlockHash, out UInt256? price))
        {
            // Use the cached price only if the timeout hasn't elapsed.
            if (!forceRefresh)
            {
                if (_logger.IsTrace) _logger.Trace($"[{ClassName}] Using cached gas price estimate: {price}");
                return price!.Value;
            }
        }
        else
        {
            // Since the head block has changed, we need to re-compute the average gas usage and
            // update the inbox ring buffer full status
            _averageGasUsage = GetAverageGasUsagePerBlock();

            if (!_isInboxRingBufferFull)
            {
                await UpdateInboxRingBufferFullAsync();
            }
        }


        // Get the fee history from the L1 client with RPC
        L1FeeHistoryResults? feeHistory = await GetL1FeeHistory();
        if (feeHistory == null || feeHistory.BaseFeePerGas.Length == 0)
        {
            if (_logger.IsTrace) _logger.Trace($"[{ClassName}] Failed to get fee history, using fallback gas price");
            return FallbackGasPrice();
        }

        // Compute TWAP for L1 base fee and blob base fee
        UInt256 twapL1BaseFee = (UInt256)feeHistory.BaseFeePerGas.Average(fee => (decimal)fee);
        UInt256 twapBlobBaseFee = feeHistory.BaseFeePerBlobGas.Length > 0
            ? (UInt256)feeHistory.BaseFeePerBlobGas.Average(fee => (decimal)fee)
            : UInt256.Zero;

        // Based on the inbox ring buffer status, use the appropriate fixed proposal gas.
        UInt256 gasRequiredForProposal = _isInboxRingBufferFull ?
            _surgeConfig.FixedProposalGasWithFullInboxBuffer
            : _surgeConfig.FixedProposalGas;

        // Compute submission cost per batch
        UInt256 submissionCostPerBatch = (gasRequiredForProposal + _surgeConfig.FixedProvingGas) * twapL1BaseFee
                                       + _surgeConfig.TargetBlobCount * BlobGasPerBlob * twapBlobBaseFee
                                       + _surgeConfig.EstimatedOffchainProvingCost;

        // Compute submission cost per block
        UInt256 submissionCostPerBlock = submissionCostPerBatch / _surgeConfig.BlocksPerBatch;

        // Reduce the average gas usage to prevent upward trend
        _averageGasUsage = _averageGasUsage * (ulong)_surgeConfig.AverageGasUsagePercentage / 100;

        UInt256 gasPriceEstimate = submissionCostPerBlock /
                                   Math.Max(_averageGasUsage, _surgeConfig.L2BlockGasTarget);

        // Adjust the gas price estimate with the config values.
        UInt256 adjustedGasPriceEstimate;
        if (_surgeConfig.SharingPercentage == 0)
        {
            adjustedGasPriceEstimate = default;
        }
        else
        {
            adjustedGasPriceEstimate = gasPriceEstimate + gasPriceEstimate * (UInt256)_surgeConfig.BoostBaseFeePercentage / 100;
            adjustedGasPriceEstimate = adjustedGasPriceEstimate * 100 / (UInt256)_surgeConfig.SharingPercentage;
        }

        // Update the cache and timestamp
        _gasPriceEstimation.Set(headBlockHash, adjustedGasPriceEstimate);
        _lastGasPriceCalculation = DateTime.UtcNow;

        if (_logger.IsDebug)
        {
            _logger.Debug($"[{ClassName}] Calculated new gas price estimate: {adjustedGasPriceEstimate}, " +
                          $"TWAP L1 Base Fee: {twapL1BaseFee}, TWAP Blob Base Fee: {twapBlobBaseFee}, " +
                          $"Submission Cost Per Block: {submissionCostPerBlock}, Average Gas Usage: {_averageGasUsage}, " +
                          $"Adjusted with boost base fee percentage of {_surgeConfig.BoostBaseFeePercentage}% " +
                          $"and sharing percentage of {_surgeConfig.SharingPercentage}%");
        }

        return adjustedGasPriceEstimate;
    }

    /// <summary>
    /// Determines if the gas price should be forced to refresh due to timeout.
    /// </summary>
    private bool ForceRefreshGasPrice()
    {
        return DateTime.UtcNow - _lastGasPriceCalculation >= TimeSpan.FromSeconds(_surgeConfig.GasPriceRefreshTimeoutSeconds);
    }

    private async ValueTask<L1FeeHistoryResults?> GetL1FeeHistory()
    {
        try
        {
            return await _l1RpcClient.Post<L1FeeHistoryResults?>("eth_feeHistory",
                _surgeConfig.FeeHistoryBlockCount,
                BlockParameter.Latest,
                null);
        }
        catch (Exception ex)
        {
            if (_logger.IsTrace) _logger.Trace($"[{ClassName}] Failed to get fee history: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get the average gas usage per block across L2GasUsageWindowSize recent blocks.
    /// </summary>
    private ulong GetAverageGasUsagePerBlock()
    {
        Block? headBlock = _blockFinder.Head;
        if (headBlock is null)
        {
            if (_logger.IsTrace) _logger.Trace($"[{ClassName}] No head block available for gas usage tracking");
            return _surgeConfig.L2BlockGasTarget;
        }

        ulong totalGasUsed = 0;
        int count = 0;
        long currentBlockNumber = headBlock.Number;

        while (count < _surgeConfig.L2GasUsageWindowSize && currentBlockNumber >= 0)
        {
            Block? block = _blockFinder.FindBlock(currentBlockNumber, BlockTreeLookupOptions.RequireCanonical);
            if (block != null)
            {
                totalGasUsed += (ulong)block.GasUsed;
                count++;
            }
            currentBlockNumber--;
        }

        ulong average = count > 0 ? totalGasUsed / (ulong)count : _surgeConfig.L2BlockGasTarget;

        if (_logger.IsTrace)
        {
            _logger.Trace($"[{ClassName}] Average gas usage: {average}, " +
                          $"Head block: {headBlock.Number}, Blocks sampled: {count}");
        }

        return average;
    }

    /// <summary>
    /// Update the inbox ring buffer full status.
    /// Ring buffer is full when: nextProposalId - lastFinalizedProposalId >= ringBufferSize
    /// </summary>
    private async ValueTask UpdateInboxRingBufferFullAsync()
    {
        if (_inboxRingBufferSize == 0)
        {
            ulong? ringBufferSize = await GetInboxRingBufferSize();
            if (ringBufferSize is null) return;
            _inboxRingBufferSize = ringBufferSize.Value;
        }

        ulong? unfinalizedCount = await GetUnfinalizedProposalCount();
        if (unfinalizedCount is null) return;

        _isInboxRingBufferFull = unfinalizedCount.Value >= _inboxRingBufferSize;
    }

    /// <summary>
    /// Get ringBufferSize from the TaikoInbox contract's getConfig() function.
    /// Config struct has ringBufferSize at word index 10 (0-indexed).
    /// </summary>
    private async ValueTask<ulong?> GetInboxRingBufferSize()
    {
        string? response = await CallTaikoInboxFunction(GetConfigHexData);

        // ringBufferSize is at word 10: offset = 2 + 10*64 = 642, length = 64
        if (string.IsNullOrEmpty(response) || response.Length < 706)
        {
            if (_logger.IsDebug) _logger.Debug($"[{ClassName}] Failed to get config, response length: {response?.Length}");
            return null;
        }

        return ulong.Parse(response[642..706], NumberStyles.HexNumber);
    }

    /// <summary>
    /// Get the count of unfinalized proposals (nextProposalId - lastFinalizedProposalId) from getCoreState().
    /// CoreState struct layout:
    ///   Word 0: nextProposalId (uint48)
    ///   Word 2: lastFinalizedProposalId (uint48)
    /// </summary>
    private async ValueTask<ulong?> GetUnfinalizedProposalCount()
    {
        string? response = await CallTaikoInboxFunction(GetCoreStateHexData);

        // Need at least 3 fields: 2 + 3*64 = 194 chars
        if (string.IsNullOrEmpty(response) || response.Length < 194)
        {
            if (_logger.IsDebug) _logger.Debug($"[{ClassName}] Failed to get core state, response length: {response?.Length}");
            return null;
        }

        // nextProposalId at [2..66]
        ulong nextProposalId = ulong.Parse(response[2..66], NumberStyles.HexNumber);
        // lastFinalizedProposalId at [130..194]
        ulong lastFinalizedProposalId = ulong.Parse(response[130..194], NumberStyles.HexNumber);

        return nextProposalId - lastFinalizedProposalId;
    }

    /// <summary>
    /// Helper method to call TaikoInbox contract functions using ABI encoding.
    /// </summary>
    private async ValueTask<string?> CallTaikoInboxFunction(string data)
    {
        try
        {
            return await _l1RpcClient.Post<string>("eth_call", new
            {
                to = _surgeConfig.TaikoInboxAddress,
                data
            }, "latest");
        }
        catch (Exception ex)
        {
            if (_logger.IsDebug) _logger.Debug($"[{ClassName}] Contract call to TaikoInbox with data: {data} failed: {ex.Message}");
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
