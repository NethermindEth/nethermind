// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth
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
                RequestPooledTransactions(send, discoveredTxHashes);
            }
        }

        public void RequestTransactionsEth66(Action<V66.Messages.GetPooledTransactionsMessage> send, IReadOnlyList<Keccak> hashes)
        {
            using ArrayPoolList<Keccak> discoveredTxHashes = new(hashes.Count);
            AddMarkUnknownHashes(hashes, discoveredTxHashes);

            if (discoveredTxHashes.Count != 0)
            {
                RequestPooledTransactionsEth66(send, discoveredTxHashes);
            }
        }

        public void RequestTransactionsEth68(Action<V66.Messages.GetPooledTransactionsMessage> send, IReadOnlyList<Keccak> hashes, IReadOnlyList<int> sizes)
        {
            using ArrayPoolList<(Keccak Hash, int Size)> discoveredTxHashesAndSizes = new(hashes.Count);
            AddMarkUnknownHashesEth68(hashes, sizes, discoveredTxHashesAndSizes);

            if (discoveredTxHashesAndSizes.Count != 0)
            {
                int packetSizeLeft = TransactionsMessage.MaxPacketSize;
                using ArrayPoolList<Keccak> hashesToRequest = new(discoveredTxHashesAndSizes.Count);

                for (int i = 0; i < discoveredTxHashesAndSizes.Count; i++)
                {
                    int txSize = discoveredTxHashesAndSizes[i].Size;

                    if (txSize > packetSizeLeft && hashesToRequest.Count > 0)
                    {
                        RequestPooledTransactionsEth66(send, hashesToRequest);
                        hashesToRequest.Clear();
                        packetSizeLeft = TransactionsMessage.MaxPacketSize;
                    }

                    hashesToRequest.Add(discoveredTxHashesAndSizes[i].Hash);
                    packetSizeLeft -= txSize;
                }

                if (hashesToRequest.Count > 0)
                {
                    RequestPooledTransactionsEth66(send, hashesToRequest);
                }
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
        private void AddMarkUnknownHashesEth68(IReadOnlyList<Keccak> hashes, IReadOnlyList<int> sizes, ArrayPoolList<(Keccak, int)> discoveredTxHashesAndSizes)
        {
            int count = hashes.Count;
            for (int i = 0; i < count; i++)
            {
                Keccak hash = hashes[i];
                if (!_txPool.IsKnown(hash) && _pendingHashes.Set(hash))
                {
                    discoveredTxHashesAndSizes.Add((hash, sizes[i]));
                }
            }
        }

        private void RequestPooledTransactions(Action<GetPooledTransactionsMessage> send, IReadOnlyList<Keccak> hashesToRequest)
        {
            send(new GetPooledTransactionsMessage(hashesToRequest));
            Metrics.Eth65GetPooledTransactionsRequested++;
        }

        private void RequestPooledTransactionsEth66(Action<V66.Messages.GetPooledTransactionsMessage> send, IReadOnlyList<Keccak> hashesToRequest)
        {
            GetPooledTransactionsMessage msg65 = new(hashesToRequest);
            send(new V66.Messages.GetPooledTransactionsMessage() { EthMessage = msg65 });
            Metrics.Eth66GetPooledTransactionsRequested++;
        }
    }
}
