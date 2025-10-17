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
        using var message = msg;
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

        TxPool.Metrics.PendingTransactionsHashesReceived += message.Hashes.Count;

        AddNotifiedTransactions(message.Hashes);

        long startTime = isTrace ? Stopwatch.GetTimestamp() : 0;

        RequestTransactions(message.Hashes, message.Sizes, message.Types);

        if (isTrace) Logger.Trace($"OUT {Counter:D5} {nameof(NewPooledTransactionHashesMessage68)} to {Node:c} in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds}ms");
    }

    public void RequestTransactions(IOwnedReadOnlyList<Hash256> hashes, IOwnedReadOnlyList<int> sizes, IOwnedReadOnlyList<byte> types)
    {
        using ArrayPoolList<(Hash256 Hash, byte Type, int Size)> discoveredTxHashesAndSizes = AddMarkUnknownHashes(hashes.AsSpan(), sizes.AsSpan(), types.AsSpan());
        if (discoveredTxHashesAndSizes.Count == 0)
        {
            hashes.Dispose();
            sizes.Dispose();
            types.Dispose();
            discoveredTxHashesAndSizes.Dispose();
            return;
        }

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

            if ((txSize > packetSizeLeft || hashesToRequest.Count >= 256) && toRequestCount > 0)
            {
                RequestPooledTransactions(hashesToRequest);
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

        RequestPooledTransactions(hashesToRequest);
    }

    private ArrayPoolList<(Hash256, byte, int)> AddMarkUnknownHashes(ReadOnlySpan<Hash256> hashes, ReadOnlySpan<int> sizes, ReadOnlySpan<byte> types)
    {
        ArrayPoolList<(Hash256, byte, int)> discoveredTxHashesAndSizes = new(hashes.Length);
        for (int i = 0; i < hashes.Length; i++)
        {
            Hash256 hash = hashes[i];
            if (!_txPool.IsKnown(hash) && !_txPool.ContainsTx(hash, (TxType)types[i]))
            {
                if (_txPool.AnnounceTx(hash, this) is AnnounceResult.New)
                {
                    discoveredTxHashesAndSizes.Add((hash, types[i], sizes[i]));
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
