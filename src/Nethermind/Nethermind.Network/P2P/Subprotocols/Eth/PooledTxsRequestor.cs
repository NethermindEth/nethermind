// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class PooledTxsRequestor(ITxPool txPool, ITxPoolConfig txPoolConfig, ISpecProvider specProvider) : IPooledTxsRequestor
    {
        private const int MaxNumberOfTxsInOneMsg = 256;
        private readonly bool _blobSupportEnabled = txPoolConfig.BlobsSupport.IsEnabled();
        private readonly long _configuredMaxTxSize = txPoolConfig.MaxTxSize ?? long.MaxValue;

        private readonly long _configuredMaxBlobTxSize = txPoolConfig.MaxBlobTxSize is null
            ? long.MaxValue
            : txPoolConfig.MaxBlobTxSize.Value + (long)specProvider.GetFinalMaxBlobGasPerBlock();

        public void RequestTransactions(Action<GetPooledTransactionsMessage> send, IOwnedReadOnlyList<Hash256> hashes, Guid sessionId)
        {
            ArrayPoolList<Hash256> discoveredTxHashes = AddMarkUnknownHashes(hashes.AsSpan(), send, sessionId);
            RequestPooledTransactions(send, discoveredTxHashes);
        }

        public void RequestTransactionsEth66(Action<V66.Messages.GetPooledTransactionsMessage> send, IOwnedReadOnlyList<Hash256> hashes, Guid sessionId)
        {
            ArrayPoolList<Hash256> discoveredTxHashes = AddMarkUnknownHashes(hashes.AsSpan(), send, sessionId);

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

        public void RequestTransactionsEth68(Action<V66.Messages.GetPooledTransactionsMessage> send, IOwnedReadOnlyList<Hash256> hashes, IOwnedReadOnlyList<int> sizes, IOwnedReadOnlyList<byte> types, Guid sessionId)
        {
            using ArrayPoolList<(Hash256 Hash, byte Type, int Size)> discoveredTxHashesAndSizes = AddMarkUnknownHashesEth68(hashes.AsSpan(), sizes.AsSpan(), types.AsSpan(), send, sessionId);
            if (discoveredTxHashesAndSizes.Count == 0) return;

            int packetSizeLeft = TransactionsMessage.MaxPacketSize;
            ArrayPoolList<Hash256> hashesToRequest = new(discoveredTxHashesAndSizes.Count);

            var discoveredCount = discoveredTxHashesAndSizes.Count;
            var toRequestCount = 0;
            foreach ((Hash256 hash, byte type, int size) in discoveredTxHashesAndSizes.AsSpan())
            {
                int txSize = size;
                TxType txType = (TxType)type;

                long maxSize = txType.SupportsBlobs() ? _configuredMaxBlobTxSize : _configuredMaxTxSize;
                if (txSize > maxSize)
                    continue;

                if (txSize > packetSizeLeft && toRequestCount > 0)
                {
                    RequestPooledTransactionsEth66(send, hashesToRequest);
                    hashesToRequest = new ArrayPoolList<Hash256>(discoveredCount);
                    packetSizeLeft = TransactionsMessage.MaxPacketSize;
                    toRequestCount = 0;
                }

                if (_blobSupportEnabled || txType != TxType.Blob)
                {
                    hashesToRequest.Add(hash);
                    packetSizeLeft -= txSize;
                    toRequestCount++;
                }
            }

            RequestPooledTransactionsEth66(send, hashesToRequest);
        }

        private ArrayPoolList<Hash256> AddMarkUnknownHashes(ReadOnlySpan<Hash256> hashes, Action<GetPooledTransactionsMessage> send, Guid sessionId)
        {
            ArrayPoolList<Hash256> discoveredTxHashes = new(hashes.Length);
            for (int i = 0; i < hashes.Length; i++)
            {
                Hash256 hash = hashes[i];
                if (!txPool.IsKnown(hash))
                {
                    if (txPool.AnnounceTx(hash, sessionId, () =>
                    {
                        ArrayPoolList<Hash256> hashesToRetry = new(1) { hash };
                        GetPooledTransactionsMessage msg65 = new(hashesToRetry);
                        send(msg65);
                    }) is AnnounceResult.New)
                    {
                        discoveredTxHashes.Add(hash);
                    }
                }
            }

            return discoveredTxHashes;
        }

        private ArrayPoolList<Hash256> AddMarkUnknownHashes(ReadOnlySpan<Hash256> hashes, Action<V66.Messages.GetPooledTransactionsMessage> send, Guid sessionId)
        {
            ArrayPoolList<Hash256> discoveredTxHashes = new(hashes.Length);
            for (int i = 0; i < hashes.Length; i++)
            {
                Hash256 hash = hashes[i];
                if (!txPool.IsKnown(hash))
                {
                    if (txPool.AnnounceTx(hash, sessionId, () =>
                    {
                        ArrayPoolList<Hash256> hashesToRetry = new(1) { hash };
                        V66.Messages.GetPooledTransactionsMessage msg66 = new(MessageConstants.Random.NextLong(), new GetPooledTransactionsMessage(hashesToRetry));
                        send(msg66);
                    }) is AnnounceResult.New)
                    {
                        discoveredTxHashes.Add(hash);
                    }
                }
            }

            return discoveredTxHashes;
        }

        private ArrayPoolList<(Hash256, byte, int)> AddMarkUnknownHashesEth68(ReadOnlySpan<Hash256> hashes, ReadOnlySpan<int> sizes, ReadOnlySpan<byte> types, Action<V66.Messages.GetPooledTransactionsMessage> send, Guid sessionId)
        {
            ArrayPoolList<(Hash256, byte, int)> discoveredTxHashesAndSizes = new(hashes.Length);
            for (int i = 0; i < hashes.Length; i++)
            {
                Hash256 hash = hashes[i];
                if (!txPool.IsKnown(hash) && !txPool.ContainsTx(hash, (TxType)types[i]))
                {
                    if (txPool.AnnounceTx(hash, sessionId, () =>
                    {
                        ArrayPoolList<Hash256> hashesToRetry = new(1) { hash };
                        V66.Messages.GetPooledTransactionsMessage msg66 = new(MessageConstants.Random.NextLong(), new GetPooledTransactionsMessage(hashesToRetry));
                        send(msg66);
                    }) is AnnounceResult.New)
                    {
                        discoveredTxHashesAndSizes.Add((hash, types[i], sizes[i]));
                    }
                }
            }

            return discoveredTxHashesAndSizes;
        }

        private static void RequestPooledTransactions(Action<GetPooledTransactionsMessage> send, IOwnedReadOnlyList<Hash256> hashesToRequest)
        {
            send(new(hashesToRequest));
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
