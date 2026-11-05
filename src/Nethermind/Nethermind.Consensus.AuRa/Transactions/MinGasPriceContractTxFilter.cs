// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class MinGasPriceContractTxFilter(IMinGasPriceTxFilter minGasPriceFilter, IDictionaryContractDataStore<TxPriorityContract.Destination> minGasPrices) : ITxFilter
    {
        private readonly IMinGasPriceTxFilter _minGasPriceFilter = minGasPriceFilter;
        private readonly IDictionaryContractDataStore<TxPriorityContract.Destination> _minGasPrices = minGasPrices ?? throw new ArgumentNullException(nameof(minGasPrices));

        public AcceptTxResult IsAllowed(Transaction tx, in TxFilterContext context)
        {
            AcceptTxResult isAllowed = _minGasPriceFilter.IsAllowed(tx, in context);
            if (!isAllowed)
            {
                return isAllowed;
            }
            else if (_minGasPrices.TryGetValue(context.Parent, tx, out TxPriorityContract.Destination @override))
            {
                return _minGasPriceFilter.IsAllowed(tx, in context, @override.Value);
            }

            return AcceptTxResult.Accepted;
        }
    }
}
