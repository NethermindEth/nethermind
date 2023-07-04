// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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
        private readonly LruKeyCache<ValueKeccak> _pendingHashes = new(MemoryAllowance.TxHashCacheSize,
            Math.Min(1024 * 16, MemoryAllowance.TxHashCacheSize), "pending tx hashes");

        public PooledTxsRequestor(ITxPool txPool)
        {
            _txPool = txPool;
        }

        public void RequestTransactions(Action<GetPooledTransactionsMessage> send, IReadOnlyList<Keccak> hashes)
        {
            using ArrayPoolList<Keccak> discoveredTxHashes = new(hashes.Count);
            AddMarkUnknownHashes(hashes, discoveredTxHashes);

            if (discoveredTxHashes.Count != 0)
            {
                send(new GetPooledTransactionsMessage(discoveredTxHashes));
                Metrics.Eth65GetPooledTransactionsRequested++;
            }
        }

        public void RequestTransactionsEth66(Action<V66.Messages.GetPooledTransactionsMessage> send, IReadOnlyList<Keccak> hashes)
        {
            using ArrayPoolList<Keccak> discoveredTxHashes = new(hashes.Count);
            AddMarkUnknownHashes(hashes, discoveredTxHashes);

            if (discoveredTxHashes.Count != 0)
            {
                GetPooledTransactionsMessage msg65 = new(discoveredTxHashes);
                send(new V66.Messages.GetPooledTransactionsMessage() { EthMessage = msg65 });
                Metrics.Eth66GetPooledTransactionsRequested++;
            }
        }

        private void AddMarkUnknownHashes(IReadOnlyList<Keccak> hashes, ArrayPoolList<Keccak> discoveredTxHashes)
        {
            int count = hashes.Count;
            for (int i = 0; i < count; i++)
            {
                Keccak hash = hashes[i];
                if (!_txPool.IsKnown(hash) && _pendingHashes.Set(hash))
                {
                    discoveredTxHashes.Add(hash);
                }
            }
        }
    }
}
