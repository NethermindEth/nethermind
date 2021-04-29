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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.TxPool
{
    public class TxPoolInfoProvider : ITxPoolInfoProvider
    {
        private readonly IStateReader _stateReader;
        private readonly ITxPool _txPool;

        public TxPoolInfoProvider(IStateReader stateReader, ITxPool txPool)
        {
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
        }

        public TxPoolInfo GetInfo(BlockHeader head)
        {
            var groupedTransactions = _txPool.GetPendingTransactionsBySender();
            var pendingTransactions = new Dictionary<Address, IDictionary<ulong, Transaction>>();
            var queuedTransactions = new Dictionary<Address, IDictionary<ulong, Transaction>>();
            foreach (KeyValuePair<Address, Transaction[]> group in groupedTransactions)
            {
                Address? address = group.Key;
                if (address is null)
                {
                    continue;
                }

                var accountNonce = _stateReader.GetNonce(head.StateRoot, address);
                var expectedNonce = accountNonce;
                var pending = new Dictionary<ulong, Transaction>();
                var queued = new Dictionary<ulong, Transaction>();
                var transactionsOrderedByNonce = group.Value.OrderBy(t => t.Nonce);

                foreach (var transaction in transactionsOrderedByNonce)
                {
                    ulong transactionNonce = (ulong) transaction.Nonce;
                    if (transaction.Nonce == expectedNonce)
                    {
                        pending.Add(transactionNonce, transaction);
                        expectedNonce = transaction.Nonce + 1;
                    }
                    else
                    {
                        queued.Add(transactionNonce, transaction);    
                    }
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

        public string GetSnapshot(BlockHeader head)
        {
            StringBuilder details = new();
            Transaction[] txpoolContent = _txPool.GetPendingTransactions();

            for (int i = 0; i < txpoolContent.Length; i++)
            {
                Transaction tx = txpoolContent[i];

                Address senderAddress = tx.SenderAddress;
                
                if (senderAddress is null)
                {
                    continue;
                }
                
                UInt256 currentNonce = _stateReader.GetNonce(head.StateRoot, senderAddress);
                UInt256 txNonce = tx.Nonce;
                UInt256 gasPrice = tx.GasPrice / 1000000000;
                UInt256 gasBottleneck = tx.GasBottleneck / 1000000000;
                long nonceDiff = (long)txNonce - (long)currentNonce;
                
                details.Append(tx.Hash);
                details.Append(',');
                details.Append(senderAddress);
                details.Append(',');
                details.Append(gasPrice);
                details.Append(',');
                details.Append(gasBottleneck);
                details.Append(',');
                details.Append(currentNonce);
                details.Append(',');
                details.Append(txNonce);
                details.Append(',');
                details.Append(nonceDiff);
                details.Append(',');
                details.Append(tx.Timestamp);
                details.Append('\n');
            }

            return details.ToString();
        }
    }
}
