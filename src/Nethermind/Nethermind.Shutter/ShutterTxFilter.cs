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
        if (tx.Type == TxType.Blob)
        {
            if (_logger.IsDebug) _logger.Debug("Decrypted Shutter transaction was blob, cannot include.");
            return AcceptTxResult.Invalid;
        }

        IReleaseSpec releaseSpec = specProvider.GetSpec(parentHeader);
        ValidationResult wellFormed = _txValidator.IsWellFormed(tx, releaseSpec);

        if (_logger.IsDebug && !wellFormed) _logger.Debug($"Decrypted Shutter transaction was not well-formed: {wellFormed}");

        return wellFormed ? AcceptTxResult.Accepted : AcceptTxResult.Invalid;
    }
}
