// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
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
using System.Threading;
using System.Threading.Tasks;

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
    : Eth67ProtocolHandler(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, forkInfo, logManager, transactionsGossipPolicy), IStaticProtocolInfo
{
    private const int MaxPooledTransactionHashesPerRequest = 256;

    private readonly bool _blobSupportEnabled = txPoolConfig.BlobsSupport.IsEnabled();
    private readonly long _configuredMaxTxSize = txPoolConfig.MaxTxSize ?? long.MaxValue;

    private readonly long _configuredMaxBlobTxSize = txPoolConfig.MaxBlobTxSize is null
        ? long.MaxValue
        : txPoolConfig.MaxBlobTxSize.Value + (long)specProvider.GetFinalMaxBlobGasPerBlock();

    private ClockCache<ValueHash256, (int, TxType)> TxShapeAnnouncements { get; } = new(MemoryAllowance.TxHashCacheSize / 10, lockPartition: 1);

    public override string Name => "eth68";

    public new static byte Version => EthVersions.Eth68;
    public override byte ProtocolVersion => Version;

    protected override bool HandleMessageCore(ZeroPacket message)
    {
        int size = message.Content.ReadableBytes;
        switch (message.PacketType)
        {
            case Eth68MessageCode.NewPooledTransactionHashes:
                if (CanReceiveTransactions)
                {
                    if (IsTransactionGossipAllowed())
                    {
                        NewPooledTransactionHashesMessage68 newPooledTxHashesMsg =
                            Deserialize<NewPooledTransactionHashesMessage68>(message.Content);
                        ReportIn(newPooledTxHashesMsg, size);
                        Handle(newPooledTxHashesMsg);
                    }
                    else
                    {
                        const string txFlooding = $"Ignoring {nameof(NewPooledTransactionHashesMessage68)} because of transaction flooding.";
                        ReportIn(txFlooding, size);
                    }
                }
                else
                {
                    const string ignored = $"{nameof(NewPooledTransactionHashesMessage68)} ignored, syncing";
                    ReportIn(ignored, size);
                }

                return true;
            default:
                return base.HandleMessageCore(message);
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

        AddNotifiedTransactions(message.Hashes.AsSpan());

        long startTime = Logger.IsTrace ? Stopwatch.GetTimestamp() : 0;

        RequestPooledTransactions(message.Hashes, message.Sizes, message.Types);

        if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} {nameof(NewPooledTransactionHashesMessage68)} to {Node:c} in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds}ms");
    }

    protected void RequestPooledTransactions(
        IOwnedReadOnlyList<Hash256> hashes,
        IOwnedReadOnlyList<int> sizes,
        IOwnedReadOnlyList<byte> types,
        bool registerForRetry = true)
    {
        ReadOnlySpan<Hash256> hashesSpan = hashes.AsSpan();
        ReadOnlySpan<int> sizesSpan = sizes.AsSpan();
        ReadOnlySpan<byte> typesSpan = types.AsSpan();

        for (int start = 0; start < hashesSpan.Length; start += MaxPooledTransactionHashesPerRequest)
        {
            int count = Math.Min(MaxPooledTransactionHashesPerRequest, hashesSpan.Length - start);
            RequestPooledTransactionsPage(
                hashesSpan.Slice(start, count),
                sizesSpan.Slice(start, count),
                typesSpan.Slice(start, count),
                registerForRetry);
        }
    }

    private void RequestPooledTransactionsPage(
        ReadOnlySpan<Hash256> hashes,
        ReadOnlySpan<int> sizes,
        ReadOnlySpan<byte> types,
        bool registerForRetry)
    {
        using ArrayPoolListRef<int> newTxHashesIndexes = AddMarkUnknownHashes(hashes, sizes, types, registerForRetry);
        if (newTxHashesIndexes.Count == 0)
        {
            return;
        }

        int packetSizeLeft = TransactionsMessage.MaxPacketSize;
        int requestCapacity = Math.Min(newTxHashesIndexes.Count, MaxPooledTransactionHashesPerRequest);
        ArrayPoolList<Hash256>? hashesToRequest = null;
        int toRequestCount = 0;

        foreach (int index in newTxHashesIndexes.AsSpan())
        {
            Hash256 hash = hashes[index];
            (int Size, TxType Type) txShape = TxShapeAnnouncements.TryGet(hash, out (int Size, TxType Type) announcedShape)
                ? announcedShape
                : (sizes[index], (TxType)types[index]);
            int txSize = txShape.Size;

            bool oversized = txSize > TransactionsMessage.MaxPacketSize;
            if (ShouldSendCurrentRequest(txSize, packetSizeLeft, toRequestCount))
            {
                SendHashesToRequest();
            }

            hashesToRequest ??= new ArrayPoolList<Hash256>(oversized ? 1 : requestCapacity);
            hashesToRequest.Add(hash);
            toRequestCount++;

            if (oversized)
            {
                SendHashesToRequest();
            }
            else
            {
                packetSizeLeft -= txSize;
            }
        }

        if (hashesToRequest is not null)
        {
            SendHashesToRequest();
        }

        void SendHashesToRequest()
        {
            ArrayPoolList<Hash256> request = hashesToRequest!;
            hashesToRequest = null;
            ReportPooledTransactionRequests(request.AsSpan());
            Send(V66.Messages.GetPooledTransactionsMessage.New(request));
            packetSizeLeft = TransactionsMessage.MaxPacketSize;
            toRequestCount = 0;
        }
    }

    public override void HandleMessages(ReadOnlySpan<ValueHash256> txHashes)
    {
        for (int start = 0; start < txHashes.Length; start += MaxPooledTransactionHashesPerRequest)
        {
            int count = Math.Min(MaxPooledTransactionHashesPerRequest, txHashes.Length - start);
            HandleMessagesPage(txHashes.Slice(start, count));
        }
    }

    private void HandleMessagesPage(ReadOnlySpan<ValueHash256> txHashes)
    {
        ArrayPoolList<Hash256>? hashesWithShape = null;
        ArrayPoolList<int>? sizes = null;
        ArrayPoolList<byte>? types = null;
        ArrayPoolList<ValueHash256>? hashesWithoutShape = null;

        try
        {
            for (int i = 0; i < txHashes.Length; i++)
            {
                ValueHash256 txHash = txHashes[i];
                if (TxShapeAnnouncements.TryGet(txHash, out (int Size, TxType Type) txShape))
                {
                    hashesWithShape ??= new ArrayPoolList<Hash256>(txHashes.Length);
                    sizes ??= new ArrayPoolList<int>(txHashes.Length);
                    types ??= new ArrayPoolList<byte>(txHashes.Length);

                    hashesWithShape.Add(new Hash256(txHash));
                    sizes.Add(txShape.Size);
                    types.Add((byte)txShape.Type);
                }
                else
                {
                    hashesWithoutShape ??= new ArrayPoolList<ValueHash256>(txHashes.Length);
                    hashesWithoutShape.Add(txHash);
                }
            }

            if (hashesWithShape is not null)
            {
                RequestPooledTransactions(hashesWithShape, sizes!, types!, registerForRetry: false);
            }

            if (hashesWithoutShape is not null)
            {
                base.HandleMessages(hashesWithoutShape.AsSpan());
            }
        }
        finally
        {
            hashesWithShape?.Dispose();
            sizes?.Dispose();
            types?.Dispose();
            hashesWithoutShape?.Dispose();
        }
    }

    private bool CanRequestPooledTransaction(TxType txType) => txType switch
    {
        TxType.Legacy or TxType.AccessList or TxType.EIP1559 or TxType.SetCode => true,
        TxType.Blob => _blobSupportEnabled,
        _ => false,
    };

    private static bool ShouldSendCurrentRequest(int txSize, int packetSizeLeft, int toRequestCount) =>
        toRequestCount >= MaxPooledTransactionHashesPerRequest
        || (txSize > packetSizeLeft && toRequestCount > 0);

    private ArrayPoolListRef<int> AddMarkUnknownHashes(
        ReadOnlySpan<Hash256> hashes,
        ReadOnlySpan<int> sizes,
        ReadOnlySpan<byte> types,
        bool registerForRetry)
    {
        ArrayPoolListRef<int> discoveredTxHashesAndSizes = new(hashes.Length);
        for (int i = 0; i < hashes.Length; i++)
        {
            Hash256 hash = hashes[i];
            if (!_txPool.IsKnown(hash))
            {
                (int Size, TxType Type) txShape = (sizes[i], (TxType)types[i]);
                if (!CanRequestPooledTransaction(txShape.Type)
                    || txShape.Size <= 0
                    || txShape.Size > (txShape.Type.SupportsBlobs() ? _configuredMaxBlobTxSize : _configuredMaxTxSize))
                {
                    continue;
                }

                if (!TxShapeAnnouncements.TryGet(hash, out _))
                {
                    TxShapeAnnouncements.Set(hash, txShape);
                }

                if (!registerForRetry || _txPool.NotifyAboutTx(hash, this) is AnnounceResult.RequestRequired)
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

    protected override ValueTask HandleSlow(TransactionsRequest request, CancellationToken cancellationToken)
    {
        IOwnedReadOnlyList<Transaction> transactions = request.Transactions;
        ReadOnlySpan<Transaction> transactionsSpan = transactions.AsSpan();
        int startIdx = request.StartIndex;
        for (int i = startIdx; i < transactionsSpan.Length; i++)
        {
            if (!ValidateSizeAndType(transactionsSpan[i]))
            {
                // [0, startIdx) were already handled in a prior scheduler slot.
                for (int j = startIdx; j < transactionsSpan.Length; j++)
                {
                    transactionsSpan[j].ClearPreHash();
                }
                transactions.Dispose();
                throw new SubprotocolException("invalid pooled tx type or size");
            }
        }

        return base.HandleSlow(request, cancellationToken);
    }

    private bool ValidateSizeAndType(Transaction? tx)
        => tx is not null && (!TxShapeAnnouncements.Delete(tx.Hash, out (int Size, TxType Type) txShape) || (tx.GetLength() == txShape.Size && tx.Type == txShape.Type));
}
