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
using Nethermind.Core.Crypto;
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
            IList<Keccak> discoveredTxHashes = GetAndMarkUnknownHashes(hashes);
            
            if (discoveredTxHashes.Count != 0)
            {
                send(new GetPooledTransactionsMessage(discoveredTxHashes.ToArray()));
                Metrics.Eth65GetPooledTransactionsRequested++;
            }
        }

        private IList<Keccak> GetAndMarkUnknownHashes(IReadOnlyList<Keccak> hashes)
        {
            List<Keccak> discoveredTxHashes = new();
            
            for (int i = 0; i < hashes.Count; i++)
            {
                Keccak hash = hashes[i];
                if (!_txPool.IsKnown(hash))
                {
                    if (!_pendingHashes.Get(hash))
                    {
                        discoveredTxHashes.Add(hash);
                        _pendingHashes.Set(hash);
                    }
                }
            }

            return discoveredTxHashes;
        }
    }
}
