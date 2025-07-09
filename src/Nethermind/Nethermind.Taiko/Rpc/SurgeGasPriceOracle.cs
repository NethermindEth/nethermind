// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
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
using Nethermind.Abi;

namespace Nethermind.Taiko.Rpc;

public class SurgeGasPriceOracle : GasPriceOracle
{
    private const string ClassName = nameof(SurgeGasPriceOracle);
    private const int BlobSize = (4 * 31 + 3) * 1024 - 4;

    // ABI signatures and encoded function selectors for TaikoInbox
    private static readonly AbiSignature GetStats2Signature = new("getStats2");
    private static readonly AbiSignature GetBatchSignature = new("getBatch", AbiType.UInt64);
    private static readonly string GetStats2HexData = "0x" + Convert.ToHexString(
        AbiEncoder.Instance.Encode(AbiEncodingStyle.IncludeSignature, GetStats2Signature));

    private readonly IJsonRpcClient _l1RpcClient;
    private readonly ISurgeConfig _surgeConfig;
    private readonly GasUsageRingBuffer _gasUsageBuffer;

    private DateTime _lastGasPriceCalculation = DateTime.MinValue;

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
        _gasUsageBuffer = new GasUsageRingBuffer(_surgeConfig.L2GasUsageWindowSize);
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
        ulong averageGasUsage;

        // Check if the cached price exists.
        if (_gasPriceEstimation.TryGetPrice(headBlockHash, out UInt256? price))
        {
            // Use the cached price only if the timeout hasn't elapsed.
            if (!forceRefresh)
            {
                if (_logger.IsTrace) _logger.Trace($"[{ClassName}] Using cached gas price estimate: {price}");
                return price!.Value;
            }

            // Since the head block has not changed, we can reuse the existing average gas usage (even with force refresh)
            averageGasUsage = _gasUsageBuffer.Average;
        }
        else
        {
            averageGasUsage = await GetAverageGasUsageAcrossBatches();
        }

        // Get the fee history from the L1 client with RPC
        L1FeeHistoryResults? feeHistory = await GetL1FeeHistory();
        if (feeHistory == null || feeHistory.BaseFeePerGas.Length == 0)
        {
            if (_logger.IsTrace) _logger.Trace($"[{ClassName}] Failed to get fee history, using fallback gas price");
            return FallbackGasPrice();
        }

        // Get the latest base fee and blob base fee from the fee history
        UInt256 l1BaseFee = feeHistory.BaseFeePerGas[^1];
        UInt256 l1BlobBaseFee = feeHistory.BaseFeePerBlobGas.Length > 0 ? feeHistory.BaseFeePerBlobGas[^1] : UInt256.Zero;
        UInt256 l1AverageBaseFee = (UInt256)feeHistory.BaseFeePerGas.Average(fee => (decimal)fee);

        // Compute the gas cost to post a batch on L1
        UInt256 costWithCallData = _surgeConfig.BatchPostingGasWithCallData * l1BaseFee;
        UInt256 costWithBlobs = _surgeConfig.BatchPostingGasWithoutCallData * l1BaseFee + BlobSize * l1BlobBaseFee;
        UInt256 minProposingCost = UInt256.Min(costWithCallData, costWithBlobs);

        UInt256 proofPostingCost = _surgeConfig.ProofPostingGas * UInt256.Max(l1BaseFee, l1AverageBaseFee);

        UInt256 gasPriceEstimate = (minProposingCost + proofPostingCost + _surgeConfig.ProvingCostPerL2Batch) /
                                   Math.Max(averageGasUsage, _surgeConfig.L2GasPerL2Batch);

        // Adjust the gas price estimate with the config values.
        UInt256 adjustedGasPriceEstimate = gasPriceEstimate + gasPriceEstimate * (UInt256)_surgeConfig.BoostBaseFeePercentage / 100;
        adjustedGasPriceEstimate = adjustedGasPriceEstimate * 100 / (UInt256)_surgeConfig.SharingPercentage;

        // Update the cache and timestamp
        _gasPriceEstimation.Set(headBlockHash, adjustedGasPriceEstimate);
        _lastGasPriceCalculation = DateTime.UtcNow;

        if (_logger.IsTrace)
        {
            _logger.Trace($"[{ClassName}] Calculated new gas price estimate: {adjustedGasPriceEstimate}, " +
                          $"L1 Base Fee: {l1BaseFee}, L1 Blob Base Fee: {l1BlobBaseFee}, " +
                          $"L1 Average Base Fee: {l1AverageBaseFee}, Average Gas Usage: {averageGasUsage}, " +
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
    /// Get the average gas usage across L2GasUsageWindowSize batches.
    /// It uses the TaikoInbox contract to get the total number of blocks in the latest proposed batch.
    /// </summary>
    private async ValueTask<ulong> GetAverageGasUsageAcrossBatches()
    {
        // Get the current batch information
        ulong? numBatches = await GetNumBatches();
        if (numBatches is null or < 1)
        {
            if (_logger.IsTrace) _logger.Trace($"[{ClassName}] Failed to get numBatches");
            return 0;
        }

        // Get the latest proposed batch and the previous batch to compute the start and end block ids
        ulong? currentBatchLastBlockId = numBatches > 1 ? await GetLastBlockId(numBatches.Value - 1) : 0;
        ulong? previousBatchLastBlockId = numBatches > 2 ? await GetLastBlockId(numBatches.Value - 2) : 0;

        if (currentBatchLastBlockId == null || previousBatchLastBlockId == null)
        {
            if (_logger.IsTrace) _logger.Trace($"[{ClassName}] Failed to get batch lastBlockId");
            return 0;
        }

        ulong startBlockId = previousBatchLastBlockId.Value == 0 ? 0 : previousBatchLastBlockId.Value + 1;
        ulong endBlockId = currentBatchLastBlockId.Value;

        // Calculate total gas used for the batch
        ulong totalGasUsed = 0;
        for (ulong blockId = startBlockId; blockId <= endBlockId; blockId++)
        {
            Block? block = _blockFinder.FindBlock((long)blockId, BlockTreeLookupOptions.RequireCanonical);
            if (block != null)
            {
                totalGasUsed += (ulong)block.GasUsed;
            }
        }

        // Record the batch's gas usage and compute the average
        _gasUsageBuffer.Add(totalGasUsed);

        if (_logger.IsTrace)
        {
            _logger.Trace($"[{ClassName}] Total gas used: {totalGasUsed}, " +
                          $"startBlockId: {startBlockId}, endBlockId: {endBlockId}, " +
                          $"Average gas usage: {_gasUsageBuffer.Average}, " +
                          $"L2GasUsageWindowSize: {_surgeConfig.L2GasUsageWindowSize}, " +
                          $"numBatches: {numBatches}, " +
                          $"startBlockId: {startBlockId}, " +
                          $"endBlockId: {endBlockId}");
        }

        return _gasUsageBuffer.Average;
    }

    /// <summary>
    /// Get the number of batches from the TaikoInbox contract's getStats2() function.
    /// </summary>
    private async ValueTask<ulong?> GetNumBatches()
    {
        string? response = await CallTaikoInboxFunction(GetStats2HexData);

        if (string.IsNullOrEmpty(response) || response.Length < 66) return null;

        // Extract the first 32 bytes (64 hex chars) after "0x" which contains NumBatches
        return ulong.Parse(response[2..66], NumberStyles.HexNumber);
    }

    /// <summary>
    /// Get the last block id from the TaikoInbox contract's getBatch(uint64) function.
    /// </summary>
    private async ValueTask<ulong?> GetLastBlockId(ulong batchId)
    {
        byte[] encodedData = AbiEncoder.Instance.Encode(AbiEncodingStyle.IncludeSignature, GetBatchSignature, batchId);
        string? response = await CallTaikoInboxFunction("0x" + Convert.ToHexString(encodedData));

        if (string.IsNullOrEmpty(response) || response.Length < 130) return null;

        // Extract the second 32 bytes (64 hex chars) after "0x" which contains LastBlockId
        return ulong.Parse(response[66..130], NumberStyles.HexNumber);
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
            if (_logger.IsTrace) _logger.Trace($"[{ClassName}] Contract call to TaikoInbox with data: {data} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// A fixed-size ring buffer for tracking gas usage and computing moving averages.
    ///
    /// +----+----+----+----+
    /// | A  | B  | C  | D  |
    /// +----+----+----+----+
    ///       ^
    /// insertAt = 1 (next insert location)
    /// average = (A + B + C + D) / 4
    /// </summary>
    private sealed class GasUsageRingBuffer(int capacity)
    {
        private readonly ulong[] _buffer = new ulong[capacity];
        private int _insertAt;
        private int _numItems;
        private ulong _sum;

        public ulong Average => _numItems == 0 ? 0 : _sum / (ulong)_numItems;

        public void Add(ulong gasUsed)
        {
            // If the buffer is full, overwrite the oldest value
            if (_numItems == _buffer.Length)
            {
                _sum -= _buffer[_insertAt];
            }
            else
            {
                _numItems++;
            }

            _buffer[_insertAt] = gasUsed;
            _sum += _buffer[_insertAt];
            _insertAt = (_insertAt + 1) % _buffer.Length;
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
