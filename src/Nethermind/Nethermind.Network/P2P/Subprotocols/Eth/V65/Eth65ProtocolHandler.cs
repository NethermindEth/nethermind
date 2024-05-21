// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V64;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65
{
    /// <summary>
    /// https://github.com/ethereum/EIPs/blob/master/EIPS/eip-2464.md
    /// </summary>
    public class Eth65ProtocolHandler : Eth64ProtocolHandler
    {
        private readonly IPooledTxsRequestor _pooledTxsRequestor;

        public Eth65ProtocolHandler(ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager nodeStatsManager,
            ISyncServer syncServer,
            IBackgroundTaskScheduler backgroundTaskScheduler,
            ITxPool txPool,
            IPooledTxsRequestor pooledTxsRequestor,
            IGossipPolicy gossipPolicy,
            ForkInfo forkInfo,
            ILogManager logManager,
            ITxGossipPolicy? transactionsGossipPolicy = null)
            : base(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, forkInfo, logManager, transactionsGossipPolicy)
        {
            _pooledTxsRequestor = pooledTxsRequestor;
        }

        public override string Name => "eth65";

        public override byte ProtocolVersion => EthVersions.Eth65;

        public override void HandleMessage(ZeroPacket message)
        {
            base.HandleMessage(message);

            int size = message.Content.ReadableBytes;
            switch (message.PacketType)
            {
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
                case Eth65MessageCode.GetPooledTransactions:
                    GetPooledTransactionsMessage getPooledTxMsg = Deserialize<GetPooledTransactionsMessage>(message.Content);
                    ReportIn(getPooledTxMsg, size);
                    BackgroundTaskScheduler.ScheduleBackgroundTask(getPooledTxMsg, Handle);
                    break;
                case Eth65MessageCode.NewPooledTransactionHashes:
                    if (CanReceiveTransactions)
                    {
                        NewPooledTransactionHashesMessage newPooledTxMsg = Deserialize<NewPooledTransactionHashesMessage>(message.Content);
                        ReportIn(newPooledTxMsg, size);
                        Handle(newPooledTxMsg);
                    }
                    else
                    {
                        const string ignored = $"{nameof(NewPooledTransactionHashesMessage)} ignored, syncing";
                        ReportIn(ignored, size);
                    }

                    break;
            }
        }

        protected virtual void Handle(NewPooledTransactionHashesMessage msg)
        {
            AddNotifiedTransactions(msg.Hashes);

            Stopwatch stopwatch = Stopwatch.StartNew();

            TxPool.Metrics.PendingTransactionsHashesReceived += msg.Hashes.Count;
            _pooledTxsRequestor.RequestTransactions(Send, msg.Hashes);

            stopwatch.Stop();
            if (Logger.IsTrace)
                Logger.Trace($"OUT {Counter:D5} {nameof(NewPooledTransactionHashesMessage)} to {Node:c} " +
                             $"in {stopwatch.Elapsed.TotalMilliseconds}ms");
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
            var stopwatch = Stopwatch.StartNew();
            Send(await FulfillPooledTransactionsRequest(message, cancellationToken));
            stopwatch.Stop();
            if (Logger.IsTrace)
                Logger.Trace($"OUT {Counter:D5} {nameof(GetPooledTransactionsMessage)} to {Node:c} " +
                             $"in {stopwatch.Elapsed.TotalMilliseconds}ms");
        }

        internal Task<PooledTransactionsMessage> FulfillPooledTransactionsRequest(GetPooledTransactionsMessage msg, CancellationToken cancellationToken)
        {
            ArrayPoolList<Transaction> txsToSend = new(msg.Hashes.Count);

            int packetSizeLeft = TransactionsMessage.MaxPacketSize;
            for (int i = 0; i < msg.Hashes.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                if (_txPool.TryGetPendingTransaction(msg.Hashes[i], out Transaction tx))
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

        private void SendNewPooledTransactionMessage(IOwnedReadOnlyList<Hash256> hashes)
        {
            NewPooledTransactionHashesMessage msg = new(hashes);
            Send(msg);
        }
    }
}
