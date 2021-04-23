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
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class MinGasPriceContractTxFilter : ITxFilter
    {
        private readonly IMinGasPriceTxFilter _minGasPriceFilter;
        private readonly IDictionaryContractDataStore<TxPriorityContract.Destination> _minGasPrices;

        public MinGasPriceContractTxFilter(IMinGasPriceTxFilter minGasPriceFilter, IDictionaryContractDataStore<TxPriorityContract.Destination> minGasPrices)
        {
            _minGasPriceFilter = minGasPriceFilter;
            _minGasPrices = minGasPrices ?? throw new ArgumentNullException(nameof(minGasPrices));
        }


        public (bool Allowed, string Reason) IsAllowed(Transaction tx, BlockHeader parentHeader)
        {
            (bool Allowed, string Reason) result = _minGasPriceFilter.IsAllowed(tx, parentHeader);
            if (!result.Allowed)
            {
                return result;
            }
            else if (_minGasPrices.TryGetValue(parentHeader, tx, out TxPriorityContract.Destination @override))
            {
                return _minGasPriceFilter.IsAllowed(tx, parentHeader, @override.Value);
            }
            
            return (true, string.Empty);
        }
    }
}
