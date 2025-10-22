// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V67;
using Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V68;

public class Eth68ProtocolHandler(ISession session,
    IMessageSerializationService serializer,
    INodeStatsManager nodeStatsManager,
    ISyncServer syncServer,
    IBackgroundTaskScheduler backgroundTaskScheduler,
    ITxPool txPool,
    IGossipPolicy gossipPolicy,
    IForkInfo forkInfo,
    ILogManager logManager,
    ITxPoolConfig txPoolConfig,
    ISpecProvider specProvider,
    ITxGossipPolicy? transactionsGossipPolicy = null
    )
    : Eth67ProtocolHandler(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, forkInfo, logManager, transactionsGossipPolicy)
{
    private readonly bool _blobSupportEnabled = txPoolConfig.BlobsSupport.IsEnabled();
    private readonly long _configuredMaxTxSize = txPoolConfig.MaxTxSize ?? long.MaxValue;

    private readonly long _configuredMaxBlobTxSize = txPoolConfig.MaxBlobTxSize is null
        ? long.MaxValue
        : txPoolConfig.MaxBlobTxSize.Value + (long)specProvider.GetFinalMaxBlobGasPerBlock();

    public override string Name => "eth68";

    public override byte ProtocolVersion => EthVersions.Eth68;

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

    private void Handle(NewPooledTransactionHashesMessage68 msg)
    {
        using NewPooledTransactionHashesMessage68 message = msg;

        if (message.Hashes.Count != message.Types.Count || message.Hashes.Count != message.Sizes.Count)
        {
            string errorMessage = $"Wrong format of {nameof(NewPooledTransactionHashesMessage68)} message. " +
                                  $"Hashes count: {message.Hashes.Count} " +
                                  $"Types count: {message.Types.Count} " +
                                  $"Sizes count: {message.Sizes.Count}";
            if (Logger.IsTrace) Logger.Trace(errorMessage);

            throw new SubprotocolException(errorMessage);
        }

        TxPool.Metrics.PendingTransactionsHashesReceived += message.Hashes.Count;

        AddNotifiedTransactions(message.Hashes);

        long startTime = Logger.IsTrace ? Stopwatch.GetTimestamp() : 0;

        RequestPooledTransactions(message.Hashes, message.Sizes, message.Types);

        if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} {nameof(NewPooledTransactionHashesMessage68)} to {Node:c} in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds}ms");
    }

    protected void RequestPooledTransactions(IOwnedReadOnlyList<Hash256> hashes, IOwnedReadOnlyList<int> sizes, IOwnedReadOnlyList<byte> types)
    {
        using ArrayPoolList<int> newTxHashesIndexes = AddMarkUnknownHashes(hashes.AsSpan());

        if (newTxHashesIndexes.Count == 0)
        {
            hashes.Dispose();
            sizes.Dispose();
            types.Dispose();

            return;
        }

        int packetSizeLeft = TransactionsMessage.MaxPacketSize;
        ArrayPoolList<Hash256> hashesToRequest = new(newTxHashesIndexes.Count);

        int discoveredCount = newTxHashesIndexes.Count;
        int toRequestCount = 0;

        foreach (int index in newTxHashesIndexes.AsSpan())
        {
            Hash256 hash = hashes[index];
            int txSize = sizes[index];
            TxType txType = (TxType)types[index];

            long maxTxSize = txType.SupportsBlobs() ? _configuredMaxBlobTxSize : _configuredMaxTxSize;

            if (txSize > maxTxSize)
                continue;

            if ((txSize > packetSizeLeft && toRequestCount > 0) || toRequestCount >= 256)
            {
                Send(V66.Messages.GetPooledTransactionsMessage.New(hashesToRequest));
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

        if (hashesToRequest.Count is not 0)
        {
            Send(V66.Messages.GetPooledTransactionsMessage.New(hashesToRequest));
        }
        else
        {
            hashesToRequest.Dispose();
        }
    }

    private ArrayPoolList<int> AddMarkUnknownHashes(ReadOnlySpan<Hash256> hashes)
    {
        ArrayPoolList<int> discoveredTxHashesAndSizes = new(hashes.Length);
        for (int i = 0; i < hashes.Length; i++)
        {
            Hash256 hash = hashes[i];
            if (!_txPool.IsKnown(hash))
            {
                if (_txPool.AnnounceTx(hash, this) is AnnounceResult.New)
                {
                    discoveredTxHashesAndSizes.Add(i);
                }
            }
        }

        return discoveredTxHashesAndSizes;
    }

    protected override void SendNewTransactionCore(Transaction tx)
    {
        if (tx.CanBeBroadcast())
        {
            base.SendNewTransactionCore(tx);
        }
        else
        {
            SendMessage(
                new ArrayPoolList<byte>(1) { (byte)tx.Type },
                new ArrayPoolList<int>(1) { tx.GetLength() },
                new ArrayPoolList<Hash256>(1) { tx.Hash }
            );
        }
    }

    protected override void SendNewTransactionsCore(IEnumerable<Transaction> txs, bool sendFullTx)
    {
        if (sendFullTx)
        {
            base.SendNewTransactionsCore(txs, sendFullTx);
            return;
        }

        ArrayPoolList<byte> types = new(NewPooledTransactionHashesMessage68.MaxCount);
        ArrayPoolList<int> sizes = new(NewPooledTransactionHashesMessage68.MaxCount);
        ArrayPoolList<Hash256> hashes = new(NewPooledTransactionHashesMessage68.MaxCount);

        foreach (Transaction tx in txs)
        {
            if (hashes.Count == NewPooledTransactionHashesMessage68.MaxCount)
            {
                SendMessage(types, sizes, hashes);
                types = new(NewPooledTransactionHashesMessage68.MaxCount);
                sizes = new(NewPooledTransactionHashesMessage68.MaxCount);
                hashes = new(NewPooledTransactionHashesMessage68.MaxCount);
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
        else
        {
            types.Dispose();
            sizes.Dispose();
            hashes.Dispose();
        }
    }

    private void SendMessage(IOwnedReadOnlyList<byte> types, IOwnedReadOnlyList<int> sizes, IOwnedReadOnlyList<Hash256> hashes)
    {
        NewPooledTransactionHashesMessage68 message = new(types, sizes, hashes);
        Send(message);
    }
}
