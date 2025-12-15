// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;

namespace Nethermind.Taiko.Tdx;

public class TdxRpcModule(
    ITdxConfig config,
    ITdxService tdxService,
    IBlockFinder blockFinder,
    ILogManager logManager) : ITdxRpcModule
{
    private static readonly ResultWrapper<TdxAttestation?> TdxDisabled =
        ResultWrapper<TdxAttestation?>.Fail("TDX is not enabled. Set Tdx.Enabled=true in configuration.");
    private static readonly ResultWrapper<TdxGuestInfo?> TdxDisabledInfo =
        ResultWrapper<TdxGuestInfo?>.Fail("TDX is not enabled. Set Tdx.Enabled=true in configuration.");

    private readonly ILogger _logger = logManager.GetClassLogger();

    public Task<ResultWrapper<TdxAttestation?>> taiko_getTdxAttestation(Hash256 blockHash)
    {
        if (!config.Enabled)
            return Task.FromResult(TdxDisabled);

        if (!tdxService.IsAvailable)
            return Task.FromResult(ResultWrapper<TdxAttestation?>.Fail("TDX service not bootstrapped. Call taiko_tdxBootstrap first."));

        Block? block = blockFinder.FindBlock(blockHash);
        if (block is null)
            return Task.FromResult(ResultWrapper<TdxAttestation?>.Fail("Block not found"));

        try
        {
            TdxAttestation attestation = tdxService.Attest(block);
            return Task.FromResult(ResultWrapper<TdxAttestation?>.Success(attestation));
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to generate TDX attestation: {ex.Message}", ex);
            return Task.FromResult(ResultWrapper<TdxAttestation?>.Fail($"Attestation failed: {ex.Message}"));
        }
    }

    public Task<ResultWrapper<TdxGuestInfo?>> taiko_getTdxGuestInfo()
    {
        if (!config.Enabled)
            return Task.FromResult(TdxDisabledInfo);

        TdxGuestInfo? info = tdxService.GetGuestInfo();
        return info is null
            ? Task.FromResult(ResultWrapper<TdxGuestInfo?>.Fail("TDX service not bootstrapped. Call taiko_tdxBootstrap first."))
            : Task.FromResult(ResultWrapper<TdxGuestInfo?>.Success(info));
    }

    public Task<ResultWrapper<TdxGuestInfo?>> taiko_tdxBootstrap()
    {
        if (!config.Enabled)
            return Task.FromResult(TdxDisabledInfo);

        try
        {
            TdxGuestInfo info = tdxService.Bootstrap();
            return Task.FromResult(ResultWrapper<TdxGuestInfo?>.Success(info));
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to bootstrap TDX: {ex.Message}", ex);
            return Task.FromResult(ResultWrapper<TdxGuestInfo?>.Fail($"Bootstrap failed: {ex.Message}"));
        }
    }
}

