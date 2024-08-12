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
        private readonly bool _blobSupportEnabled;

        private readonly ClockKeyCache<ValueHash256> _pendingHashes = new(MemoryAllowance.TxHashCacheSize);

        public PooledTxsRequestor(ITxPool txPool, ITxPoolConfig txPoolConfig)
        {
            _txPool = txPool;
            _blobSupportEnabled = txPoolConfig.BlobsSupport.IsEnabled();
        }

        public void RequestTransactions(Action<GetPooledTransactionsMessage> send, IReadOnlyList<Hash256> hashes)
        {
            ArrayPoolList<Hash256> discoveredTxHashes = AddMarkUnknownHashes(hashes);
            RequestPooledTransactions(send, discoveredTxHashes);
        }

        public void RequestTransactionsEth66(Action<V66.Messages.GetPooledTransactionsMessage> send, IReadOnlyList<Hash256> hashes)
        {
            ArrayPoolList<Hash256> discoveredTxHashes = AddMarkUnknownHashes(hashes);

            if (discoveredTxHashes.Count <= MaxNumberOfTxsInOneMsg)
            {
                RequestPooledTransactionsEth66(send, discoveredTxHashes);
            }
            else
            {
                using ArrayPoolList<Hash256> _ = discoveredTxHashes;

                for (int start = 0; start < discoveredTxHashes.Count; start += MaxNumberOfTxsInOneMsg)
                {
                    var end = Math.Min(start + MaxNumberOfTxsInOneMsg, discoveredTxHashes.Count);

                    ArrayPoolList<Hash256> hashesToRequest = new(end - start);
                    hashesToRequest.AddRange(discoveredTxHashes.AsSpan()[start..end]);
                    RequestPooledTransactionsEth66(send, hashesToRequest);
                }
            }
        }

        public void RequestTransactionsEth68(Action<V66.Messages.GetPooledTransactionsMessage> send, IReadOnlyList<Hash256> hashes, IReadOnlyList<int> sizes, IReadOnlyList<byte> types)
        {
            using ArrayPoolList<(Hash256 Hash, byte Type, int Size)> discoveredTxHashesAndSizes = AddMarkUnknownHashesEth68(hashes, sizes, types);
            if (discoveredTxHashesAndSizes.Count == 0) return;

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

                if (_blobSupportEnabled || txType != TxType.Blob)
                {
                    hashesToRequest.Add(discoveredTxHashesAndSizes[i].Hash);
                    packetSizeLeft -= txSize;
                }
            }

            RequestPooledTransactionsEth66(send, hashesToRequest);
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
            if (hashesToRequest.Count > 0)
            {
                send(new(hashesToRequest));
            }
            else
            {
                hashesToRequest.Dispose();
            }
        }

        private static void RequestPooledTransactionsEth66(Action<V66.Messages.GetPooledTransactionsMessage> send, IOwnedReadOnlyList<Hash256> hashesToRequest)
        {
            if (hashesToRequest.Count > 0)
            {
                GetPooledTransactionsMessage msg65 = new(hashesToRequest);
                send(new() { EthMessage = msg65 });
            }
            else
            {
                hashesToRequest.Dispose();
            }
        }
    }
}
