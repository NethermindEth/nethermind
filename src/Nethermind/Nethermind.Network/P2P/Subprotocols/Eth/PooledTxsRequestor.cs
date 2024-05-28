// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
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
        private const int MaxNumberOfTxsInOneMsg = 256;
        private readonly ITxPool _txPool;
        private readonly ITxPoolConfig _txPoolConfig;

        private readonly LruKeyCacheLowObject<ValueHash256> _pendingHashes = new(MemoryAllowance.TxHashCacheSize, "pending tx hashes");

        public PooledTxsRequestor(ITxPool txPool, ITxPoolConfig txPoolConfig)
        {
            _txPool = txPool;
            _txPoolConfig = txPoolConfig;
        }

        public void RequestTransactions(Action<GetPooledTransactionsMessage> send, IReadOnlyList<Hash256> hashes)
        {
            ArrayPoolList<Hash256> discoveredTxHashes = AddMarkUnknownHashes(hashes);

            if (discoveredTxHashes.Count != 0)
            {
                RequestPooledTransactions(send, discoveredTxHashes);
            }
        }

        public void RequestTransactionsEth66(Action<V66.Messages.GetPooledTransactionsMessage> send, IReadOnlyList<Hash256> hashes)
        {
            using ArrayPoolList<Hash256> discoveredTxHashes = AddMarkUnknownHashes(hashes);

            if (discoveredTxHashes.Count != 0)
            {
                if (discoveredTxHashes.Count <= MaxNumberOfTxsInOneMsg)
                {
                    RequestPooledTransactionsEth66(send, discoveredTxHashes);
                }
                else
                {
                    ArrayPoolList<Hash256> hashesToRequest = new(MaxNumberOfTxsInOneMsg);
                    for (int i = 0; i < discoveredTxHashes.Count; i++)
                    {
                        if (hashesToRequest.Count % MaxNumberOfTxsInOneMsg == 0 && hashesToRequest.Count > 0)
                        {
                            RequestPooledTransactionsEth66(send, hashesToRequest);
                            hashesToRequest = new(MaxNumberOfTxsInOneMsg);
                        }

                        hashesToRequest.Add(discoveredTxHashes[i]);
                    }

                    if (hashesToRequest.Count > 0)
                    {
                        RequestPooledTransactionsEth66(send, hashesToRequest);
                    }
                }
            }
        }

        public void RequestTransactionsEth68(Action<V66.Messages.GetPooledTransactionsMessage> send, IReadOnlyList<Hash256> hashes, IReadOnlyList<int> sizes, IReadOnlyList<byte> types)
        {
            using ArrayPoolList<(Hash256 Hash, byte Type, int Size)> discoveredTxHashesAndSizes = AddMarkUnknownHashesEth68(hashes, sizes, types);

            if (discoveredTxHashesAndSizes.Count != 0)
            {
                int packetSizeLeft = TransactionsMessage.MaxPacketSize;
                ArrayPoolList<Hash256> hashesToRequest = new(discoveredTxHashesAndSizes.Count);

                for (int i = 0; i < discoveredTxHashesAndSizes.Count; i++)
                {
                    int txSize = discoveredTxHashesAndSizes[i].Size;
                    TxType txType = (TxType)discoveredTxHashesAndSizes[i].Type;

                    if (txSize > packetSizeLeft && hashesToRequest.Count > 0)
                    {
                        RequestPooledTransactionsEth66(send, hashesToRequest);
                        hashesToRequest = new ArrayPoolList<Hash256>(discoveredTxHashesAndSizes.Count);
                        packetSizeLeft = TransactionsMessage.MaxPacketSize;
                    }

                    if (_txPoolConfig.BlobsSupport.IsEnabled() || txType != TxType.Blob)
                    {
                        hashesToRequest.Add(discoveredTxHashesAndSizes[i].Hash);
                        packetSizeLeft -= txSize;
                    }
                }

                if (hashesToRequest.Count > 0)
                {
                    RequestPooledTransactionsEth66(send, hashesToRequest);
                }
            }
        }

        private ArrayPoolList<Hash256> AddMarkUnknownHashes(IReadOnlyList<Hash256> hashes)
        {
            int count = hashes.Count;
            ArrayPoolList<Hash256> discoveredTxHashes = new ArrayPoolList<Hash256>(count);
            for (int i = 0; i < count; i++)
            {
                Hash256 hash = hashes[i];
                if (!_txPool.IsKnown(hash) && _pendingHashes.Set(hash))
                {
                    discoveredTxHashes.Add(hash);
                }
            }

            return discoveredTxHashes;
        }

        private ArrayPoolList<(Hash256, byte, int)> AddMarkUnknownHashesEth68(IReadOnlyList<Hash256> hashes, IReadOnlyList<int> sizes, IReadOnlyList<byte> types)
        {
            int count = hashes.Count;
            ArrayPoolList<(Hash256, byte, int)> discoveredTxHashesAndSizes = new(count);
            for (int i = 0; i < count; i++)
            {
                Hash256 hash = hashes[i];
                if (!_txPool.IsKnown(hash) && !_txPool.ContainsTx(hash, (TxType)types[i]) && _pendingHashes.Set(hash))
                {
                    discoveredTxHashesAndSizes.Add((hash, types[i], sizes[i]));
                }
            }

            return discoveredTxHashesAndSizes;
        }

        private static void RequestPooledTransactions(Action<GetPooledTransactionsMessage> send, IOwnedReadOnlyList<Hash256> hashesToRequest)
        {
            send(new GetPooledTransactionsMessage(hashesToRequest));
        }

        private static void RequestPooledTransactionsEth66(Action<V66.Messages.GetPooledTransactionsMessage> send, IOwnedReadOnlyList<Hash256> hashesToRequest)
        {
            GetPooledTransactionsMessage msg65 = new(hashesToRequest);
            send(new V66.Messages.GetPooledTransactionsMessage { EthMessage = msg65 });
        }
    }
}
