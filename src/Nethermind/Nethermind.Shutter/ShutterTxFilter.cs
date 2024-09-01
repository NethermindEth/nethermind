// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Transactions;
using Nethermind.TxPool;

namespace Nethermind.Shutter;

public class ShutterTxFilter(
    ISpecProvider specProvider,
    ILogManager logManager) : ITxFilter
{
    private readonly TxValidator _txValidator = new(specProvider.ChainId);
    private readonly ILogger _logger = logManager.GetClassLogger();

    public AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader)
    {
        IReleaseSpec releaseSpec = specProvider.GetSpec(parentHeader);
        bool wellFormed = _txValidator.IsWellFormed(tx, releaseSpec, out string? error);

        if (_logger.IsDebug)
        {
            if (!wellFormed) _logger.Debug($"Decrypted Shutter transaction was not well-formed{(error is null ? "." : ": " + error)}");
            if (tx.Type == TxType.Blob) _logger.Debug("Decrypted Shutter transaction was blob, cannot include.");
        }

        if (wellFormed && tx.Type != TxType.Blob)
        {
            return AcceptTxResult.Accepted;
        }

        return AcceptTxResult.Invalid;
    }
}
