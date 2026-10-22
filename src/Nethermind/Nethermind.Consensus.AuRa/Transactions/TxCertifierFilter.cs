// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Abi;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Resettables;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa.Transactions;

public class TxCertifierFilter(ICertifierContract certifierContract, ITxFilter notCertifiedFilter, ILogManager logManager) : ITxFilter
{
    private readonly ResettableDictionary<Address, bool> _certifiedCache = new(8);
    private readonly ILogger _logger = logManager.GetClassLogger<TxCertifierFilter>();
    private Hash256 _cachedBlock;

    public AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader, IReleaseSpec currentSpec) =>
        IsCertified(tx, parentHeader, currentSpec) ? AcceptTxResult.Accepted : notCertifiedFilter.IsAllowed(tx, parentHeader, currentSpec);

    private bool IsCertified(Transaction tx, BlockHeader parentHeader, IReleaseSpec currentSpec)
    {
        Address sender = tx.SenderAddress;
        if (tx.IsZeroGasPrice(currentSpec) && sender is not null)
        {
            if (_logger.IsTrace) _logger.Trace($"Checking service transaction checker contract from {sender}.");
            IDictionary<Address, bool> cache = GetCache(parentHeader.Hash);

            if (cache.TryGetValue(sender, out bool isCertified))
            {
                tx.IsServiceTransaction = isCertified;
                return isCertified;
            }

            try
            {
                bool isCertifiedByContract = certifierContract.Certified(parentHeader, sender);
                tx.IsServiceTransaction = isCertifiedByContract;
                return cache[sender] = isCertifiedByContract;
            }
            catch (AbiException e)
            {
                if (_logger.IsError) _logger.Error($"Call to certifier contract failed on block {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)}.", e);
                return false;
            }
        }

        return false;
    }

    private IDictionary<Address, bool> GetCache(Hash256 blockHash)
    {
        if (blockHash != _cachedBlock)
        {
            _certifiedCache.Reset();
            _cachedBlock = blockHash;
        }

        return _certifiedCache;
    }
}
