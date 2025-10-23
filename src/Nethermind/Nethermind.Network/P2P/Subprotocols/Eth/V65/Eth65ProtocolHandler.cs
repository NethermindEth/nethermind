// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Contract.Messages;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V64;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65
{
    /// <summary>
    /// https://github.com/ethereum/EIPs/blob/master/EIPS/eip-2464.md
    /// </summary>
    public class Eth65ProtocolHandler(
        ISession session,
        IMessageSerializationService serializer,
        INodeStatsManager nodeStatsManager,
        ISyncServer syncServer,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        ITxPool txPool,
        IGossipPolicy gossipPolicy,
        IForkInfo forkInfo,
        ILogManager logManager,
        ITxGossipPolicy? transactionsGossipPolicy = null)
        : Eth64ProtocolHandler(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, forkInfo, logManager, transactionsGossipPolicy),
          IMessageHandler<PooledTransactionRequestMessage>
    {
        public override string Name => "eth65";

        public override byte ProtocolVersion => EthVersions.Eth65;

        private const int MaxNumberOfTxsInOneMsg = 256;

        public override void HandleMessage(ZeroPacket message)
        {
            base.HandleMessage(message);

            int size = message.Content.ReadableBytes;
            switch (message.PacketType)
            {
                case Eth65MessageCode.NewPooledTransactionHashes:
                    if (CanReceiveTransactions)
                    {
                        using NewPooledTransactionHashesMessage newPooledTxMsg = Deserialize<NewPooledTransactionHashesMessage>(message.Content);
                        ReportIn(newPooledTxMsg, size);
                        Handle(newPooledTxMsg);
                    }
                    else
                    {
                        const string ignored = $"{nameof(NewPooledTransactionHashesMessage)} ignored, syncing";
                        ReportIn(ignored, size);
                    }

                    break;
                case Eth65MessageCode.GetPooledTransactions:
                    HandleInBackground<GetPooledTransactionsMessage>(message, Handle);
                    break;
                case Eth65MessageCode.PooledTransactions:
                    if (CanReceiveTransactions)
                    {
                        PooledTransactionsMessage pooledTxMsg = Deserialize<PooledTransactionsMessage>(message.Content);
                        ReportIn(pooledTxMsg, size);
                        Handle(pooledTxMsg);
                    }
                    else
                    {
                        const string ignored = $"{nameof(PooledTransactionsMessage)} ignored, syncing";
                        ReportIn(ignored, size);
                    }

                    break;
            }
        }

        protected virtual void Handle(NewPooledTransactionHashesMessage msg)
        {
            RequestPooledTransactions<GetPooledTransactionsMessage>(msg.Hashes);
        }

        protected void AddNotifiedTransactions(IReadOnlyList<Hash256> hashes)
        {
            foreach (Hash256 hash in hashes)
            {
                NotifiedTransactions.Set(hash);
            }
        }

        private async ValueTask Handle(GetPooledTransactionsMessage msg, CancellationToken cancellationToken)
        {
            using var message = msg;
            long startTime = Stopwatch.GetTimestamp();
            Send(await FulfillPooledTransactionsRequest(message, cancellationToken));
            if (Logger.IsTrace)
                Logger.Trace($"OUT {Counter:D5} {nameof(GetPooledTransactionsMessage)} to {Node:c} " +
                             $"in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");
        }

        internal Task<PooledTransactionsMessage> FulfillPooledTransactionsRequest(GetPooledTransactionsMessage msg, CancellationToken cancellationToken)
        {
            ArrayPoolList<Transaction> txsToSend = new(msg.Hashes.Count);

            int packetSizeLeft = TransactionsMessage.MaxPacketSize;
            foreach (Hash256 hash in msg.Hashes.AsSpan())
            {
                if (cancellationToken.IsCancellationRequested) break;

                if (_txPool.TryGetPendingTransaction(hash, out Transaction tx))
                {
                    int txSize = tx.GetLength();

                    if (txSize > packetSizeLeft && txsToSend.Count > 0)
                    {
                        break;
                    }

                    txsToSend.Add(tx);
                    packetSizeLeft -= txSize;
                    TxPool.Metrics.PendingTransactionsSent++;
                }
            }

            return Task.FromResult(new PooledTransactionsMessage(txsToSend));
        }

        protected override void SendNewTransactionsCore(IEnumerable<Transaction> txs, bool sendFullTx)
        {
            void SendNewPooledTransactionMessage(IOwnedReadOnlyList<Hash256> hashes) => Send(new NewPooledTransactionHashesMessage(hashes));

            if (sendFullTx)
            {
                base.SendNewTransactionsCore(txs, true);
                return;
            }

            ArrayPoolList<Hash256> hashes = new(NewPooledTransactionHashesMessage.MaxCount);

            foreach (Transaction tx in txs)
            {
                if (hashes.Count == NewPooledTransactionHashesMessage.MaxCount)
                {
                    SendNewPooledTransactionMessage(hashes);
                    hashes = new(NewPooledTransactionHashesMessage.MaxCount);
                }

                if (tx.Hash is not null)
                {
                    hashes.Add(tx.Hash);
                    TxPool.Metrics.PendingTransactionsHashesSent++;
                }
            }

            if (hashes.Count > 0)
            {
                SendNewPooledTransactionMessage(hashes);
            }
            else
            {
                hashes.Dispose();
            }
        }

        protected virtual void RequestPooledTransactions<GetPooledTransactionsMessage>(IOwnedReadOnlyList<Hash256> hashes)
            where GetPooledTransactionsMessage : P2PMessage, INew<IOwnedReadOnlyList<Hash256>, GetPooledTransactionsMessage>
        {
            AddNotifiedTransactions(hashes);

            long startTime = Stopwatch.GetTimestamp();
            TxPool.Metrics.PendingTransactionsHashesReceived += hashes.Count;

            ArrayPoolList<Hash256> newTxHashes = AddMarkUnknownHashes(hashes.AsSpan());

            if (newTxHashes.Count is 0)
            {
                newTxHashes.Dispose();
                return;
            }

            if (newTxHashes.Count <= MaxNumberOfTxsInOneMsg)
            {
                Send(GetPooledTransactionsMessage.New(newTxHashes));
            }
            else
            {
                try
                {
                    for (int start = 0; start < newTxHashes.Count; start += MaxNumberOfTxsInOneMsg)
                    {
                        int end = Math.Min(start + MaxNumberOfTxsInOneMsg, newTxHashes.Count);

                        ArrayPoolList<Hash256> hashesToRequest = new(end - start);
                        hashesToRequest.AddRange(newTxHashes.AsSpan()[start..end]);

                        Send(GetPooledTransactionsMessage.New(hashesToRequest));
                    }
                }
                finally
                {
                    newTxHashes.Dispose();
                }
            }

            if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} {nameof(NewPooledTransactionHashesMessage)} to {Node:c} " +
                                             $"in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");
        }

        private ArrayPoolList<Hash256> AddMarkUnknownHashes(ReadOnlySpan<Hash256> hashes)
        {
            ArrayPoolList<Hash256> discoveredTxHashesAndSizes = new(hashes.Length);
            for (int i = 0; i < hashes.Length; i++)
            {
                Hash256 hash = hashes[i];
                if (!_txPool.IsKnown(hash))
                {
                    if (_txPool.AnnounceTx(hash, this) is AnnounceResult.New)
                    {
                        discoveredTxHashesAndSizes.Add(hash);
                    }
                }
            }

            return discoveredTxHashesAndSizes;
        }

        public virtual void HandleMessage(PooledTransactionRequestMessage message)
        {
            ArrayPoolList<Hash256> hashesToRetry = new(1) { new Hash256(message.TxHash) };
            RequestPooledTransactions<GetPooledTransactionsMessage>(hashesToRetry);
        }
    }
}
