//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class PermissionTxPoolSelectionStrategy : TxPoolTxSource.ITxPoolSelectionStrategy
    {
        private readonly IContractDataStore<Address> _sendersWhitelist;
        private readonly IContractDataStore<TxPriorityContract.Destination> _priorities;
        private readonly ITxFilter _minGasPriceFilter;
        private ILogger _logger;

        public PermissionTxPoolSelectionStrategy(
            IContractDataStore<Address> sendersWhitelist, 
            IContractDataStore<TxPriorityContract.Destination> priorities,
            in ITxFilter minGasPriceFilter, 
            ILogManager logManager)
        {
            _sendersWhitelist = sendersWhitelist ?? throw new ArgumentNullException(nameof(sendersWhitelist));
            _priorities = priorities ?? throw new ArgumentNullException(nameof(priorities));;
            _minGasPriceFilter = minGasPriceFilter ?? throw new ArgumentNullException(nameof(minGasPriceFilter));
            _logger = logManager?.GetClassLogger<PermissionTxPoolSelectionStrategy>() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public IEnumerable<Transaction> Select(BlockHeader blockHeader, IEnumerable<Transaction> transactions)
        {
            var sendersWhitelist = _sendersWhitelist.GetItems(blockHeader);
            var priorities = _priorities.GetItems(blockHeader);
            return transactions;
        }
    }
}
