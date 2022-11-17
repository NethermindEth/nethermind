//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.P2P.Subprotocols.Eth.V67;
using Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
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
        ISpecProvider specProvider,
        ILogManager logManager)
        : base(session, serializer, nodeStatsManager, syncServer, txPool, pooledTxsRequestor, gossipPolicy,
            specProvider, logManager)
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
            if (Logger.IsTrace)
                Logger.Trace($"Wrong format of {nameof(NewPooledTransactionHashesMessage68)} message. " +
                             $"Hashes count: {message.Hashes.Count} " +
                             $"Types count: {message.Types.Count} " +
                             $"Sizes count: {message.Sizes.Count}");

            throw new SubprotocolException($"Wrong format of {nameof(NewPooledTransactionHashesMessage68)} message. " +
                                           $"Hashes count: {message.Hashes.Count} " +
                                           $"Types count: {message.Types.Count} " +
                                           $"Sizes count: {message.Sizes.Count}");
        }

        Metrics.Eth68NewPooledTransactionHashesReceived++;

        Stopwatch stopwatch = Stopwatch.StartNew();

        _pooledTxsRequestor.RequestTransactionsEth68(Send, message.Hashes.ToArray(), message.Sizes.ToArray(),
            message.Types.ToArray());

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

        List<TxType> types = new();
        List<int> sizes = new();
        List<Keccak> hashes = new();

        TxDecoder txDecoder = new();

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
                types.Add(tx.Type);
                sizes.Add(tx.GetSize(txDecoder));
                hashes.Add(tx.Hash);
            }
        }

        if (hashes.Count != 0)
        {
            SendMessage(types, sizes, hashes);
        }
    }

    private void SendMessage(IReadOnlyList<TxType> types, IReadOnlyList<int> sizes, IReadOnlyList<Keccak> hashes)
    {
        NewPooledTransactionHashesMessage68 message = new(types, sizes, hashes);
        Metrics.Eth68NewPooledTransactionHashesSent++;
        Send(message);
    }
}
