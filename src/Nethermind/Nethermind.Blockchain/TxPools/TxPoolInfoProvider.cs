/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Store;

namespace Nethermind.Blockchain.TxPools
{
    public class TxPoolInfoProvider : ITxPoolInfoProvider
    {
        private readonly IStateProvider _stateProvider;
        private readonly ITxPool _txPool;

        public TxPoolInfoProvider(IStateProvider stateProvider, ITxPool txPool)
        {
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
        }

        public TxPoolInfo GetInfo()
        {
            var transactions = _txPool.GetPendingTransactions();
            var groupedTransactions = transactions.GroupBy(t => t.SenderAddress);
            var pendingTransactions = new Dictionary<Address, IDictionary<ulong, Transaction[]>>();
            var queuedTransactions = new Dictionary<Address, IDictionary<ulong, Transaction[]>>();
            foreach (var group in groupedTransactions)
            {
                var address = group.Key;
                if (address == null)
                {
                    continue;
                }

                var accountNonce = _stateProvider.GetNonce(address);
                var expectedNonce = accountNonce;
                var pending = new Dictionary<ulong, Transaction[]>();
                var queued = new Dictionary<ulong, Transaction[]>();
                var transactionsGroupedByNonce = group.OrderBy(t => t.Nonce).GroupBy(t => (ulong)t.Nonce);

                foreach (var nonceGroup in transactionsGroupedByNonce)
                {
                    if (nonceGroup.Key < accountNonce)
                    {
                        queued.Add(nonceGroup.Key, nonceGroup.ToArray());
                        continue;
                    }

                    if (nonceGroup.Key == accountNonce ||
                        accountNonce != expectedNonce && nonceGroup.Key == expectedNonce)
                    {
                        pending.Add(nonceGroup.Key, nonceGroup.ToArray());
                        expectedNonce = nonceGroup.Key + 1;
                        continue;
                    }

                    queued.Add(nonceGroup.Key, nonceGroup.ToArray());
                }

                if (pending.Any())
                {
                    pendingTransactions[address] = pending;
                }

                if (queued.Any())
                {
                    queuedTransactions[address] = queued;
                }
            }

            return new TxPoolInfo(pendingTransactions, queuedTransactions);
        }
    }
}