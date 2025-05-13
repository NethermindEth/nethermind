// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Optimism.CL.L1Bridge;

namespace Nethermind.Optimism.CL;

public class L1ConfigValidator : IL1ConfigValidator
{
    private readonly IEthApi _ethApi;
    private readonly ILogger _logger;

    public L1ConfigValidator(IEthApi ethApi, ILogManager logManager)
    {
        _ethApi = ethApi;
        _logger = logManager.GetClassLogger();
    }

    public async Task<bool> Validate(ulong expectedChainId, ulong genesisNumber, Hash256 expectedGenesisHash)
    {
        ulong actualChainId = await _ethApi.GetChainId();
        if (actualChainId != expectedChainId)
        {
            if (_logger.IsWarn) _logger.Warn($"L1 chain ID mismatch. Expected: {expectedChainId}, Got: {actualChainId}");
            return false;
        }

        // TODO: `IEthApi.GetBlockByNumber` currently does not support `fullTxs = false` when deserializing,
        // so we're forced to use `fullTxs = true` despite not needing it.
        var genesisBlock = await _ethApi.GetBlockByNumber(genesisNumber, true);
        if (genesisBlock is null)
        {
            if (_logger.IsWarn) _logger.Warn("Failed to get L1 genesis block");
            return false;
        }

        if (genesisBlock.Value.Hash != expectedGenesisHash)
        {
            if (_logger.IsWarn) _logger.Warn($"L1 genesis hash mismatch. Expected: {expectedGenesisHash}, Got: {genesisBlock.Value.Hash}");
            return false;
        }

        return true;
    }
}
