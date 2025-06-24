// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
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
    private const int BlobSize = (4 * 31+3) * 1024 - 4;

    // ABI signatures and encoded function selectors for TaikoInbox
    private static readonly AbiSignature GetStats2Signature = new("getStats2");
    private static readonly AbiSignature GetBatchSignature = new("getBatch", AbiType.UInt64);
    private static readonly string GetStats2HexData = "0x" + Convert.ToHexString(
        AbiEncoder.Instance.Encode(AbiEncodingStyle.IncludeSignature, GetStats2Signature));

    private readonly IJsonRpcClient _l1RpcClient;
    private readonly ISurgeConfig _surgeConfig;
    private readonly GasUsageRingBuffer _gasUsageBuffer;

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

    public override UInt256 GetGasPriceEstimate()
    {
        return GetGasPriceEstimateAsync().GetAwaiter().GetResult();
    }

    public override async ValueTask<UInt256> GetGasPriceEstimateAsync()
    {
        Block? headBlock = _blockFinder.Head;
        if (headBlock is null)
        {
            if (_logger.IsTrace) _logger.Trace($"[{ClassName}] No head block available, using fallback gas price");
            return FallbackGasPrice();
        }

        Hash256 headBlockHash = headBlock.Hash!;
        if (_gasPriceEstimation.TryGetPrice(headBlockHash, out UInt256? price))
        {
            if (_logger.IsTrace) _logger.Trace($"[{ClassName}] Using cached gas price estimate: {price}");
            return price!.Value;
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
        UInt256 costWithBlobs = (_surgeConfig.BatchPostingGasWithoutCallData * l1BaseFee) + (BlobSize * l1BlobBaseFee);
        UInt256 minProposingCost = UInt256.Min(costWithCallData, costWithBlobs);

        UInt256 proofPostingCost = _surgeConfig.ProofPostingGas * UInt256.Max(l1BaseFee, l1AverageBaseFee);

        UInt256 averageGasUsage = await GetAverageGasUsageAcrossBatches();
        if (averageGasUsage == UInt256.Zero)
        {
            if (_logger.IsTrace) _logger.Trace($"[{ClassName}] Failed to calculate average gas usage, using fallback gas price");
            return FallbackGasPrice();
        }

        UInt256 gasPriceEstimate = (minProposingCost + proofPostingCost + _surgeConfig.ProvingCostPerL2Batch) / averageGasUsage;
        _gasPriceEstimation.Set(headBlockHash, gasPriceEstimate);

        if (_logger.IsTrace) _logger.Trace($"[{ClassName}] Calculated new gas price estimate: {gasPriceEstimate}, " +
            $"L1 Base Fee: {l1BaseFee}, L1 Blob Base Fee: {l1BlobBaseFee}, L1 Average Base Fee: {l1AverageBaseFee}, " +
            $"Average Gas Usage: {averageGasUsage}");

        return gasPriceEstimate;
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
    private async ValueTask<UInt256> GetAverageGasUsageAcrossBatches()
    {
        // Get the current batch information
        ValueTask<ulong?> numBatchesTask = GetNumBatches();
        ulong? numBatches = await numBatchesTask;
        if (numBatches is null or <= 1)
        {
            if (_logger.IsTrace) _logger.Trace($"[{ClassName}] Failed to get numBatches, using fallback gas price");
            return UInt256.Zero;
        }

        // Get the latest proposed batch
        ValueTask<ulong?> currentBatchLastBlockIdTask = GetLastBlockId(numBatches.Value - 1);
        ulong? currentBatchLastBlockId = await currentBatchLastBlockIdTask;
        if (currentBatchLastBlockId == null)
        {
            if (_logger.IsTrace) _logger.Trace($"[{ClassName}] Failed to get current batch lastBlockId, using fallback gas price");
            return UInt256.Zero;
        }

        // Get the previous batch to find the total number of blocks in the latest proposed batch
        ulong? previousBatchLastBlockId = numBatches > 2 ? await GetLastBlockId(numBatches.Value - 2) : 0;
        ulong startBlockId = (previousBatchLastBlockId ?? 0) + 1;
        ulong endBlockId = currentBatchLastBlockId.Value;

        // Calculate total gas used for the batch
        UInt256 totalGasUsed = UInt256.Zero;
        for (ulong blockId = startBlockId; blockId <= endBlockId; blockId++)
        {
            Block? block = _blockFinder.FindBlock((long)blockId, Blockchain.BlockTreeLookupOptions.RequireCanonical);
            if (block != null)
            {
                totalGasUsed += (UInt256)block.GasUsed;
            }
        }

        // Record the batch's gas usage and compute the average
        _gasUsageBuffer.Add((ulong)totalGasUsed);
        return _gasUsageBuffer.Average;
    }

    /// <summary>
    /// Get the number of batches from the TaikoInbox contract's getStats2() function.
    /// </summary>
    private async ValueTask<ulong?> GetNumBatches()
    {
        var response = await CallTaikoInboxFunction(GetStats2HexData);

        if (string.IsNullOrEmpty(response) || response.Length < 66) return null;

        // Extract the first 32 bytes (64 hex chars) after "0x" which contains NumBatches
        return ulong.Parse(response[2..66], System.Globalization.NumberStyles.HexNumber);
    }

    /// <summary>
    /// Get the last block id from the TaikoInbox contract's getBatch(uint64) function.
    /// </summary>
    private async ValueTask<ulong?> GetLastBlockId(ulong batchId)
    {
        var encodedData = AbiEncoder.Instance.Encode(AbiEncodingStyle.IncludeSignature, GetBatchSignature, batchId);
        var response = await CallTaikoInboxFunction("0x" + Convert.ToHexString(encodedData));

        if (string.IsNullOrEmpty(response) || response.Length < 130) return null;

        // Extract the second 32 bytes (64 hex chars) after "0x" which contains LastBlockId
        return ulong.Parse(response[66..130], System.Globalization.NumberStyles.HexNumber);
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
            if (_logger.IsTrace) _logger.Trace($"[{ClassName}] Contract call to TaikoInbox failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// A fixed-size ring buffer for tracking gas usage and computing moving averages.
    /// </summary>
    private sealed class GasUsageRingBuffer
    {
        private readonly UInt256[] _buffer;
        private int _index;
        private int _count;
        private UInt256 _total;

        public int Count => _count;
        public UInt256 Average => _count == 0 ? UInt256.Zero : _total / (ulong)_count;

        public GasUsageRingBuffer(int capacity)
        {
            _buffer = new UInt256[capacity];
            _index = 0;
            _count = 0;
            _total = UInt256.Zero;
        }

        public void Add(ulong gasUsed)
        {
            // If the buffer is full, remove the oldest value
            if (_count == _buffer.Length)
            {
                _total -= _buffer[_index];
            }
            else
            {
                _count++;
            }

            _buffer[_index] = (UInt256)gasUsed;
            _total += _buffer[_index];
            _index = (_index + 1) % _buffer.Length;
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
