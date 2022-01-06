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
using System.Linq;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65
{
    public class PooledTxsRequestor : IPooledTxsRequestor
    {
        private readonly ITxPool _txPool;
        private readonly LruKeyCache<Keccak> _pendingHashes = new(MemoryAllowance.TxHashCacheSize,
            Math.Min(1024 * 16, MemoryAllowance.TxHashCacheSize), "pending tx hashes");

        public PooledTxsRequestor(ITxPool txPool)
        {
            _txPool = txPool;
        }
        
        public void RequestTransactions(Action<GetPooledTransactionsMessage> send, IReadOnlyList<Keccak> hashes)
        {
            using ArrayPoolList<Keccak> discoveredTxHashes = new(hashes.Count, GetAndMarkUnknownHashes(hashes));
            
            if (discoveredTxHashes.Count != 0)
            {
                send(new GetPooledTransactionsMessage(discoveredTxHashes));
                Metrics.Eth65GetPooledTransactionsRequested++;
            }
        }

        public void RequestTransactionsEth66(Action<V66.Messages.GetPooledTransactionsMessage> send, IReadOnlyList<Keccak> hashes)
        {
            using ArrayPoolList<Keccak> discoveredTxHashes = new(hashes.Count, GetAndMarkUnknownHashes(hashes)); 

            if (discoveredTxHashes.Count != 0)
            {
                GetPooledTransactionsMessage msg65 = new(discoveredTxHashes);
                send(new V66.Messages.GetPooledTransactionsMessage() {EthMessage = msg65});
                Metrics.Eth66GetPooledTransactionsRequested++;
            }
        }
        
        private IEnumerable<Keccak> GetAndMarkUnknownHashes(IReadOnlyList<Keccak> hashes)
        {
            for (int i = 0; i < hashes.Count; i++)
            {
                Keccak hash = hashes[i];
                if (!_txPool.IsKnown(hash) && _pendingHashes.Set(hash))
                {
                    yield return hash;
                }
            }
        }
    }
}
