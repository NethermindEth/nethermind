// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
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
        private readonly LruKeyCache<KeccakKey> _pendingHashes = new(MemoryAllowance.TxHashCacheSize,
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
                send(new V66.Messages.GetPooledTransactionsMessage() { EthMessage = msg65 });
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
