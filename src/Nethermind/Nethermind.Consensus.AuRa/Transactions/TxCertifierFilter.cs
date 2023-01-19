// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Abi;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Resettables;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class TxCertifierFilter : ITxFilter
    {
        private readonly ICertifierContract _certifierContract;
        private readonly ITxFilter _notCertifiedFilter;
        private readonly ISpecProvider _specProvider;
        private readonly ResettableDictionary<Address, bool> _certifiedCache = new ResettableDictionary<Address, bool>(8);
        private readonly ILogger _logger;
        private Keccak _cachedBlock;

        public TxCertifierFilter(ICertifierContract certifierContract, ITxFilter notCertifiedFilter, ISpecProvider specProvider, ILogManager logManager)
        {
            _certifierContract = certifierContract ?? throw new ArgumentNullException(nameof(certifierContract));
            _notCertifiedFilter = notCertifiedFilter ?? throw new ArgumentNullException(nameof(notCertifiedFilter));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logger = logManager?.GetClassLogger<TxCertifierFilter>() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader) =>
            IsCertified(tx, parentHeader) ? AcceptTxResult.Accepted : _notCertifiedFilter.IsAllowed(tx, parentHeader);

        private bool IsCertified(Transaction tx, BlockHeader parentHeader)
        {
            Address sender = tx.SenderAddress;
            if (tx.IsZeroGasPrice(parentHeader, _specProvider) && sender is not null)
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
                    bool isCertifiedByContract = _certifierContract.Certified(parentHeader, sender);
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

        private IDictionary<Address, bool> GetCache(Keccak blockHash)
        {
            if (blockHash != _cachedBlock)
            {
                _certifiedCache.Reset();
                _cachedBlock = blockHash;
            }

            return _certifiedCache;
        }
    }
}
