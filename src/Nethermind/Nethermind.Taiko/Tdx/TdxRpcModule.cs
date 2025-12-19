// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Taiko.Config;

namespace Nethermind.Taiko.Tdx;

public class TdxRpcModule(
    ISurgeConfig config,
    ITdxService tdxService,
    IBlockFinder blockFinder,
    ILogManager logManager) : ITdxRpcModule
{
    private static readonly ResultWrapper<TdxGuestInfo> TdxDisabledInfo =
        ResultWrapper<TdxGuestInfo>.Fail("TDX is not enabled. Set Surge.TdxEnabled=true in configuration.");

    private readonly ILogger _logger = logManager.GetClassLogger();

    public Task<ResultWrapper<BlockHashTdxAttestation>> taiko_getBlockHashTdxAttestation(BlockParameter blockParameter)
    {
        string? availabilityError = VerifyTdxAvailability();
        if (availabilityError is not null)
            return Task.FromResult(ResultWrapper<BlockHashTdxAttestation>.Fail(availabilityError));

        BlockHeader? blockHeader = FindBlockHeader(blockParameter);
        if (blockHeader is null)
            return Task.FromResult(ResultWrapper<BlockHashTdxAttestation>.Fail("Block not found"));

        try
        {
            BlockHashTdxAttestation attestation = tdxService.AttestBlockHash(blockHeader.Hash!);
            return Task.FromResult(ResultWrapper<BlockHashTdxAttestation>.Success(attestation));
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to generate TDX attestation: {ex.Message}", ex);
            return Task.FromResult(ResultWrapper<BlockHashTdxAttestation>.Fail($"Attestation failed: {ex.Message}"));
        }
    }

    public Task<ResultWrapper<BlockHeaderTdxAttestation>> taiko_getBlockHeaderTdxAttestation(BlockParameter blockParameter)
    {
        string? availabilityError = VerifyTdxAvailability();
        if (availabilityError is not null)
            return Task.FromResult(ResultWrapper<BlockHeaderTdxAttestation>.Fail(availabilityError));

        BlockHeader? blockHeader = FindBlockHeader(blockParameter);
        if (blockHeader is null)
            return Task.FromResult(ResultWrapper<BlockHeaderTdxAttestation>.Fail("Block not found"));

        try
        {
            BlockHeaderTdxAttestation attestation = tdxService.AttestBlockHeader(blockHeader);
            return Task.FromResult(ResultWrapper<BlockHeaderTdxAttestation>.Success(attestation));
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to generate TDX attestation: {ex.Message}", ex);
            return Task.FromResult(ResultWrapper<BlockHeaderTdxAttestation>.Fail($"Attestation failed: {ex.Message}"));
        }
    }

    private string? VerifyTdxAvailability()
    {
        if (!config.TdxEnabled)
            return "TDX is not enabled. Set Surge.TdxEnabled=true in configuration.";

        return !tdxService.IsBootstrapped ? "TDX service not bootstrapped. Call taiko_tdxBootstrap first." : null;
    }

    private BlockHeader? FindBlockHeader(BlockParameter blockParameter)
    {
        // Only allow valid canonical blocks for TDX attestation
        BlockHeader? blockHeader;
        BlockTreeLookupOptions options = BlockTreeLookupOptions.RequireCanonical
                                        | BlockTreeLookupOptions.TotalDifficultyNotNeeded
                                        | BlockTreeLookupOptions.ExcludeTxHashes;

        if (blockParameter.BlockHash is { } blockHash)
        {
            blockHeader = blockFinder.FindHeader(blockHash, options);
        }
        else if (blockParameter.BlockNumber is { } blockNumber)
        {
            blockHeader = blockFinder.FindHeader(blockNumber, options);
        }
        else
        {
            BlockHeader? header = blockFinder.FindHeader(blockParameter);
            blockHeader = header is null ? null : blockFinder.FindHeader(header.Hash!, options);
        }

        return blockHeader;
    }

    public Task<ResultWrapper<TdxGuestInfo>> taiko_getTdxGuestInfo()
    {
        if (!config.TdxEnabled)
            return Task.FromResult(TdxDisabledInfo);

        TdxGuestInfo? info = tdxService.GetGuestInfo();
        return info is null
            ? Task.FromResult(ResultWrapper<TdxGuestInfo>.Fail("TDX service not bootstrapped. Call taiko_tdxBootstrap first."))
            : Task.FromResult(ResultWrapper<TdxGuestInfo>.Success(info));
    }

    public Task<ResultWrapper<TdxGuestInfo>> taiko_tdxBootstrap()
    {
        if (!config.TdxEnabled)
            return Task.FromResult(TdxDisabledInfo);

        try
        {
            TdxGuestInfo info = tdxService.Bootstrap();
            return Task.FromResult(ResultWrapper<TdxGuestInfo>.Success(info));
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to bootstrap TDX: {ex.Message}", ex);
            return Task.FromResult(ResultWrapper<TdxGuestInfo>.Fail($"Bootstrap failed: {ex.Message}"));
        }
    }
}
