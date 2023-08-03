// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.P2P.Subprotocols.Eth.V67;
using Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V68;

public class Eth68ProtocolHandler : Eth67ProtocolHandler
{
    private readonly IPooledTxsRequestor _pooledTxsRequestor;

    private readonly Action<V66.Messages.GetPooledTransactionsMessage> _sendAction;

    public override string Name => "eth68";

    public override byte ProtocolVersion => EthVersions.Eth68;

    public Eth68ProtocolHandler(ISession session,
        IMessageSerializationService serializer,
        INodeStatsManager nodeStatsManager,
        ISyncServer syncServer,
        ITxPool txPool,
        IPooledTxsRequestor pooledTxsRequestor,
        IGossipPolicy gossipPolicy,
        ForkInfo forkInfo,
        ILogManager logManager,
        ITxGossipPolicy? transactionsGossipPolicy = null)
        : base(session, serializer, nodeStatsManager, syncServer, txPool, pooledTxsRequestor, gossipPolicy, forkInfo, logManager, transactionsGossipPolicy)
    {
        _pooledTxsRequestor = pooledTxsRequestor;

        // Capture Action once rather than per call
        _sendAction = Send<V66.Messages.GetPooledTransactionsMessage>;
    }

    public override void HandleMessage(ZeroPacket message)
    {
        int size = message.Content.ReadableBytes;
        switch (message.PacketType)
        {
            case Eth68MessageCode.NewPooledTransactionHashes:
                if (CanReceiveTransactions)
                {
                    NewPooledTransactionHashesMessage68 newPooledTxHashesMsg =
                        Deserialize<NewPooledTransactionHashesMessage68>(message.Content);
                    ReportIn(newPooledTxHashesMsg, size);
                    Handle(newPooledTxHashesMsg);
                }
                else
                {
                    const string ignored = $"{nameof(NewPooledTransactionHashesMessage68)} ignored, syncing";
                    ReportIn(ignored, size);
                }

                break;
            default:
                base.HandleMessage(message);
                break;
        }
    }

    private void Handle(NewPooledTransactionHashesMessage68 message)
    {
        bool isTrace = Logger.IsTrace;
        if (message.Hashes.Count != message.Types.Count || message.Hashes.Count != message.Sizes.Count)
        {
            string errorMessage = $"Wrong format of {nameof(NewPooledTransactionHashesMessage68)} message. " +
                                  $"Hashes count: {message.Hashes.Count} " +
                                  $"Types count: {message.Types.Count} " +
                                  $"Sizes count: {message.Sizes.Count}";
            if (isTrace) Logger.Trace(errorMessage);

            throw new SubprotocolException(errorMessage);
        }

        Metrics.Eth68NewPooledTransactionHashesReceived++;

        AddNotifiedTransactions(message.Hashes);

        Stopwatch? stopwatch = isTrace ? Stopwatch.StartNew() : null;

        _pooledTxsRequestor.RequestTransactionsEth68(_sendAction, message.Hashes, message.Sizes);

        stopwatch?.Stop();

        if (isTrace) Logger.Trace($"OUT {Counter:D5} {nameof(NewPooledTransactionHashesMessage68)} to {Node:c} in {stopwatch.Elapsed.TotalMilliseconds}ms");
    }

    protected override void SendNewTransactionCore(Transaction tx)
    {
        if (tx.CanBeBroadcast())
        {
            base.SendNewTransactionCore(tx);
        }
        else
        {
            SendMessage(new byte[] { (byte)tx.Type }, new int[] { tx.GetLength() }, new Keccak[] { tx.Hash });
        }
    }

    protected override void SendNewTransactionsCore(IEnumerable<Transaction> txs, bool sendFullTx)
    {
        if (sendFullTx)
        {
            base.SendNewTransactionsCore(txs, sendFullTx);
            return;
        }

        using ArrayPoolList<byte> types = new(NewPooledTransactionHashesMessage68.MaxCount);
        using ArrayPoolList<int> sizes = new(NewPooledTransactionHashesMessage68.MaxCount);
        using ArrayPoolList<Keccak> hashes = new(NewPooledTransactionHashesMessage68.MaxCount);

        foreach (Transaction tx in txs)
        {
            if (hashes.Count == NewPooledTransactionHashesMessage68.MaxCount)
            {
                SendMessage(types, sizes, hashes);
                types.Clear();
                sizes.Clear();
                hashes.Clear();
            }

            if (tx.Hash is not null)
            {
                types.Add((byte)tx.Type);
                sizes.Add(tx.GetLength());
                hashes.Add(tx.Hash);
                TxPool.Metrics.PendingTransactionsHashesSent++;
            }
        }

        if (hashes.Count != 0)
        {
            SendMessage(types, sizes, hashes);
        }
    }

    private void SendMessage(IReadOnlyList<byte> types, IReadOnlyList<int> sizes, IReadOnlyList<Keccak> hashes)
    {
        NewPooledTransactionHashesMessage68 message = new(types, sizes, hashes);
        Metrics.Eth68NewPooledTransactionHashesSent++;
        Send(message);
    }

    public override void AnnounceTransactions(IEnumerable<TxAnnouncement> txAnnouncements)
    {
        using ArrayPoolList<byte> types = new(NewPooledTransactionHashesMessage68.MaxCount);
        using ArrayPoolList<int> sizes = new(NewPooledTransactionHashesMessage68.MaxCount);
        using ArrayPoolList<Keccak> hashes = new(NewPooledTransactionHashesMessage68.MaxCount);

        foreach (TxAnnouncement txAnnouncement in txAnnouncements)
        {
            if (hashes.Count == NewPooledTransactionHashesMessage68.MaxCount)
            {
                SendMessage(types, sizes, hashes);
                types.Clear();
                sizes.Clear();
                hashes.Clear();
            }

            types.Add((byte)txAnnouncement.TxType);
            sizes.Add(txAnnouncement.Size);
            hashes.Add(txAnnouncement.Hash);
            TxPool.Metrics.PendingTransactionsHashesSent++;
        }

        if (hashes.Count != 0)
        {
            SendMessage(types, sizes, hashes);
        }
    }
}
