// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.TxPool;

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


        public AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader)
        {
            AcceptTxResult isAllowed = _minGasPriceFilter.IsAllowed(tx, parentHeader);
            if (!isAllowed)
            {
                return isAllowed;
            }
            else if (_minGasPrices.TryGetValue(parentHeader, tx, out TxPriorityContract.Destination @override))
            {
                return _minGasPriceFilter.IsAllowed(tx, parentHeader, @override.Value);
            }

            return AcceptTxResult.Accepted;
        }
    }
}
