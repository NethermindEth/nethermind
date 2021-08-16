
//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
        
        public (bool Allowed, string Reason) IsAllowed(Transaction tx, BlockHeader parentHeader) => 
            IsCertified(tx, parentHeader) ? (true, string.Empty) : _notCertifiedFilter.IsAllowed(tx, parentHeader);

        private bool IsCertified(Transaction tx, BlockHeader parentHeader)
        {
            bool isEip1559Enabled = _specProvider.GetSpec(parentHeader.Number + 1).IsEip1559Enabled;
            bool checkByFeeCap = isEip1559Enabled && tx.IsEip1559;
            if (checkByFeeCap && !tx.MaxFeePerGas.IsZero) // only 0 gas price transactions are system transactions and can be whitelissted
            {
                return false;
            }
            else if (!tx.GasPrice.IsZero && !checkByFeeCap)
            {
                return false;
            }
            
            Address sender = tx.SenderAddress;
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
