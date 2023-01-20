// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.P2P.Subprotocols.Eth.V67;
using Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V68;

public class Eth68ProtocolHandler : Eth67ProtocolHandler
{
    private readonly IPooledTxsRequestor _pooledTxsRequestor;

    public override string Name => "eth68";

    public override byte ProtocolVersion => 68;

    public Eth68ProtocolHandler(ISession session,
        IMessageSerializationService serializer,
        INodeStatsManager nodeStatsManager,
        ISyncServer syncServer,
        ITxPool txPool,
        IPooledTxsRequestor pooledTxsRequestor,
        IGossipPolicy gossipPolicy,
        ForkInfo forkInfo,
        ILogManager logManager)
        : base(session, serializer, nodeStatsManager, syncServer, txPool, pooledTxsRequestor, gossipPolicy,
            forkInfo, logManager)
    {
        _pooledTxsRequestor = pooledTxsRequestor;
    }

    public override void HandleMessage(ZeroPacket message)
    {
        switch (message.PacketType)
        {
            case Eth68MessageCode.NewPooledTransactionHashes:
                NewPooledTransactionHashesMessage68 newPooledTxHashesMsg =
                    Deserialize<NewPooledTransactionHashesMessage68>(message.Content);
                ReportIn(newPooledTxHashesMsg);
                Handle(newPooledTxHashesMsg);
                break;
            default:
                base.HandleMessage(message);
                break;
        }
    }

    private void Handle(NewPooledTransactionHashesMessage68 message)
    {
        if (message.Hashes.Count != message.Types.Count || message.Hashes.Count != message.Sizes.Count)
        {
            string errorMessage = $"Wrong format of {nameof(NewPooledTransactionHashesMessage68)} message. " +
                                  $"Hashes count: {message.Hashes.Count} " +
                                  $"Types count: {message.Types.Count} " +
                                  $"Sizes count: {message.Sizes.Count}";
            if (Logger.IsTrace)
                Logger.Trace(errorMessage);

            throw new SubprotocolException(errorMessage);
        }

        Metrics.Eth68NewPooledTransactionHashesReceived++;

        Stopwatch stopwatch = Stopwatch.StartNew();

        _pooledTxsRequestor.RequestTransactionsEth66(Send, message.Hashes);

        stopwatch.Stop();

        if (Logger.IsTrace)
            Logger.Trace($"OUT {Counter:D5} {nameof(NewPooledTransactionHashesMessage68)} to {Node:c} " +
                         $"in {stopwatch.Elapsed.TotalMilliseconds}ms");
    }

    public override void SendNewTransactions(IEnumerable<Transaction> txs, bool sendFullTx)
    {
        if (sendFullTx)
        {
            base.SendNewTransactions(txs, sendFullTx);
            return;
        }

        ArrayPoolList<byte> types = new(NewPooledTransactionHashesMessage68.MaxCount);
        ArrayPoolList<int> sizes = new(NewPooledTransactionHashesMessage68.MaxCount);
        ArrayPoolList<Keccak> hashes = new(NewPooledTransactionHashesMessage68.MaxCount);

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
                sizes.Add(tx.GetLength(_txDecoder));
                hashes.Add(tx.Hash);
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
}
