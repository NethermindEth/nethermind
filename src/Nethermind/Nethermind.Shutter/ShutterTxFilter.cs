// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Consensus.Validators;
using Nethermind.TxPool;
using Nethermind.Int256;

namespace Nethermind.Shutter;

public class ShutterTxFilter(
    ISpecProvider specProvider,
    ILogManager logManager)
{
    private readonly TxValidator _txValidator = new(specProvider.ChainId);
    private readonly ILogger _logger = logManager.GetClassLogger();

    public AcceptTxResult IsAllowed(Transaction tx, UInt256 gasLimit, BlockHeader parentHeader)
    {
        if (tx.Type == TxType.Blob)
        {
            if (_logger.IsDebug) _logger.Debug("Decrypted Shutter transaction was blob, cannot include.");
            return AcceptTxResult.Invalid;
        }

        IReleaseSpec releaseSpec = specProvider.GetSpec(parentHeader);
        ValidationResult wellFormed = _txValidator.IsWellFormed(tx, releaseSpec);

        if (!wellFormed)
        {
            _logger.Debug($"Decrypted Shutter transaction was not well-formed: {wellFormed}");
            return AcceptTxResult.Invalid;
        }

        if (tx.GasLimit != gasLimit)
        {
            _logger.Debug($"Decrypted Shutter transaction gas limit {tx.GasLimit} did not match encrypted gas limit {gasLimit}.");
            return AcceptTxResult.Invalid;
        }

        return wellFormed ? AcceptTxResult.Accepted : AcceptTxResult.Invalid;
    }
}
