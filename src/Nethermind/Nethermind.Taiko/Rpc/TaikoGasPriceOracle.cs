// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.L2.Common.L1Rpc;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.Taiko.Rpc;

public class TaikoGasPriceOracle : IGasPriceOracle
{
    private readonly IBlockFinder _blockFinder;
    private readonly ISpecProvider _specProvider;
    private readonly IFeeHistoryOracle _feeHistoryOracle;
    private readonly ILogger _logger;
    private readonly IEthApi _l1Client;
    internal PriceCache _gasPriceEstimation;
    private readonly UInt256 _minGasPrice;

    // Constants required for computing the gas price
    private static readonly UInt256 L2GasPerL2Batch = 1_000_000;
    private const ulong ProvingCostPerL2Batch = 800_000_000_000_000;
    private const ulong BatchPostingGasWithoutCallData = 180_000;
    private const ulong BatchPostingGasWithCallData = 260_000;
    private const ulong ProofPostingGas = 750_000;
    private const int FeeHistoryBlockCount = 200; // Number of blocks to consider for average base fee
    private const int BlobSize = 128 * 1024;

    public TaikoGasPriceOracle(
        IBlockFinder blockFinder,
        ISpecProvider specProvider,
        IFeeHistoryOracle feeHistoryOracle,
        ILogManager logManager,
        UInt256 minGasPrice,
        IEthApi l1Client)
    {
        _blockFinder = blockFinder;
        _specProvider = specProvider;
        _feeHistoryOracle = feeHistoryOracle;
        _logger = logManager.GetClassLogger();
        _minGasPrice = minGasPrice;
        _l1Client = l1Client;
    }

    private UInt256 FallbackGasPrice() => _gasPriceEstimation.LastPrice ?? _minGasPrice;

    public UInt256 GetGasPriceEstimate()
    {
        Block? headBlock = _blockFinder.Head;
        if (headBlock is null)
        {
            if (_logger.IsTrace) _logger.Trace("[TaikoGasPriceOracle] No head block available, using fallback gas price");
            return FallbackGasPrice();
        }

        Hash256 headBlockHash = headBlock.Hash!;
        if (_gasPriceEstimation.TryGetPrice(headBlockHash, out UInt256? price))
        {
            if (_logger.IsTrace) _logger.Trace($"[TaikoGasPriceOracle] Using cached gas price estimate: {price}");
            return price!.Value;
        }

        UInt256 l1BaseFee = GetL1BaseFee().GetAwaiter().GetResult();
        UInt256 l1BlobBaseFee = GetL1BlobBaseFee().GetAwaiter().GetResult();
        UInt256 l1AverageBaseFee = GetL1AverageBaseFee().GetAwaiter().GetResult();

        // Gas cost to post a batch on L1
        UInt256 costWithCallData = BatchPostingGasWithCallData * l1BaseFee;
        UInt256 costWithBlobs = (BatchPostingGasWithoutCallData * l1BaseFee) + (BlobSize * l1BlobBaseFee);
        UInt256 minCost = UInt256.Min(costWithCallData, costWithBlobs);

        UInt256 proofPostingCost = ProofPostingGas * UInt256.Max(l1BaseFee, l1AverageBaseFee);

        UInt256 gasPriceEstimate = (minCost + proofPostingCost + ProvingCostPerL2Batch) / L2GasPerL2Batch;
        _gasPriceEstimation.Set(headBlockHash, gasPriceEstimate);

        if (_logger.IsTrace) _logger.Trace($"[TaikoGasPriceOracle] Calculated new gas price estimate: {gasPriceEstimate}, " +
            $"L1 Base Fee: {l1BaseFee}, L1 Blob Base Fee: {l1BlobBaseFee}, L1 Average Base Fee: {l1AverageBaseFee}");

        return gasPriceEstimate;
    }

    private async Task<UInt256> GetL1BaseFee()
    {
        try
        {
            var block = await _l1Client.GetHead(false);
            return block?.BaseFeePerGas ?? UInt256.Zero;
        }
        catch (Exception ex)
        {
            if (_logger.IsTrace) _logger.Trace($"[TaikoGasPriceOracle] Failed to get L1 base fee: {ex.Message}");
            return UInt256.Zero;
        }
    }

    private async Task<UInt256> GetL1BlobBaseFee()
    {
        try
        {
            return await _l1Client.GetBlobBaseFee();
        }
        catch (Exception ex)
        {
            if (_logger.IsTrace) _logger.Trace($"[TaikoGasPriceOracle] Failed to get L1 blob base fee: {ex.Message}");
            return UInt256.Zero;
        }
    }

    private async Task<UInt256> GetL1AverageBaseFee()
    {
        try
        {
            L1FeeHistoryResults? l1FeeHistory = await _l1Client.GetFeeHistory(FeeHistoryBlockCount, BlockParameter.Latest, null);
            if (l1FeeHistory?.BaseFeePerGas == null || l1FeeHistory.BaseFeePerGas.Length == 0)
            {
                if (_logger.IsTrace) _logger.Trace("[TaikoGasPriceOracle] Failed to get fee history for average base fee calculation");
                return UInt256.Zero;
            }

            var average = (UInt256)l1FeeHistory.BaseFeePerGas.Average(fee => (decimal)fee);

            if (_logger.IsTrace) _logger.Trace($"[TaikoGasPriceOracle] Calculated L1 average base fee: {average}, " +
                $"Based on {l1FeeHistory.BaseFeePerGas.Length} blocks");
            return average;
        }
        catch (Exception ex)
        {
            if (_logger.IsTrace) _logger.Trace($"[TaikoGasPriceOracle] Failed to calculate average base fee: {ex.Message}");
            return UInt256.Zero;
        }
    }

    public UInt256 GetMaxPriorityGasFeeEstimate()
    {
        // TODO: EIP-1559 support?
        return UInt256.Zero;
    }
}
