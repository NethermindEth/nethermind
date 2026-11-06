// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V67;
using Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
    IChainHeadSpecProvider specProvider,
    ITxGossipPolicy? transactionsGossipPolicy = null
    )
    : Eth67ProtocolHandler(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, forkInfo, logManager, transactionsGossipPolicy), IStaticProtocolInfo
{
    private const int MaxPooledTransactionHashesPerRequest = 256;
    private static readonly int PooledTransactionsResponseSoftLimit = (int)2.MiB;

    protected readonly bool _blobSupportEnabled = txPoolConfig.BlobsSupport.IsEnabled();
    protected readonly long _configuredMaxTxSize = txPoolConfig.MaxTxSize ?? long.MaxValue;

    protected readonly long _configuredMaxBlobTxSize = txPoolConfig.MaxBlobTxSize is null
        ? long.MaxValue
        : txPoolConfig.MaxBlobTxSize.Value + (long)specProvider.GetFinalMaxBlobGasPerBlock();

    // A fixed 8192 entries, allocated on the first announcement. Lossy: a lost shape
    // only skips the delivery check, and writes and lookups allocate nothing. A delivered shape is not removed but
    // overwritten with a zero-size sentinel.
    private const int TxShapeAnnouncementsSetsBits = 12;
    private SeqlockCache<ValueHash256, (int Size, TxType Type)>? _txShapeAnnouncements;

    /// <summary>Gets the shape a still-undelivered transaction was first announced with.</summary>
    protected bool TryGetTxShape(in ValueHash256 hash, out (int Size, TxType Type) shape)
    {
        shape = default;
        // A zero size marks a taken shape: announcements without a positive size are never stored.
        return Volatile.Read(ref _txShapeAnnouncements)?.TryGetValue(in hash, out shape) == true && shape.Size > 0;
    }

    protected void SetTxShape(in ValueHash256 hash, (int Size, TxType Type) shape) =>
        LazyInitializer.EnsureInitialized(ref _txShapeAnnouncements, static () => new(TxShapeAnnouncementsSetsBits)).Set(in hash, shape);

    /// <summary>Removes and returns the announced shape a delivered transaction is checked against.</summary>
    protected bool TryTakeTxShape(in ValueHash256 hash, out (int Size, TxType Type) shape)
    {
        if (!TryGetTxShape(in hash, out shape)) return false;

        _txShapeAnnouncements!.Set(in hash, default);
        return true;
    }

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

        foreach (ref readonly ValueHash256 hash in message.Hashes.AsSpan())
            NotifiedTransactions.Set(in hash);

        long startTime = Logger.IsTrace ? Stopwatch.GetTimestamp() : 0;

        RequestPooledTransactions(message.Hashes, message.Sizes, message.Types);

        if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} {nameof(NewPooledTransactionHashesMessage68)} to {Node?.ToString("c"):hide} in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds}ms");
    }

    protected void RequestPooledTransactions(
        IOwnedReadOnlyList<ValueHash256> hashes,
        IOwnedReadOnlyList<int> sizes,
        IOwnedReadOnlyList<byte> types,
        bool registerForRetry = true)
    {
        ReadOnlySpan<int> sizesSpan = sizes.AsSpan();
        ReadOnlySpan<byte> typesSpan = types.AsSpan();

        for (int start = 0; start < hashes.Count; start += MaxPooledTransactionHashesPerRequest)
        {
            int count = Math.Min(MaxPooledTransactionHashesPerRequest, hashes.Count - start);
            RequestPooledTransactionsPage(
                hashes, start,
                sizesSpan.Slice(start, count),
                typesSpan.Slice(start, count),
                registerForRetry);
        }
    }

    private void RequestPooledTransactionsPage(
        IOwnedReadOnlyList<ValueHash256> hashes,
        int start,
        ReadOnlySpan<int> sizes,
        ReadOnlySpan<byte> types,
        bool registerForRetry)
    {
        using ArrayPoolListRef<int> newTxHashesIndexes = AddMarkUnknownHashes(hashes, start, sizes, types, registerForRetry);
        if (newTxHashesIndexes.Count == 0)
        {
            return;
        }

        int packetSizeLeft = TransactionsMessage.MaxPacketSize;
        // Retry batches must remain compatible with peers that still use the 100 KiB response target.
        int responseSizeLimit = registerForRetry ? PooledTransactionsResponseSoftLimit : TransactionsMessage.MaxPacketSize;
        int responseSizeLeft = responseSizeLimit;
        int requestCapacity = Math.Min(newTxHashesIndexes.Count, MaxPooledTransactionHashesPerRequest);
        ArrayPoolList<ValueHash256>? hashesToRequest = null;
        ReadOnlySpan<ValueHash256> valueHashes = hashes.AsSpan();
        int toRequestCount = 0;
        bool hasOversizedTransaction = false;

        foreach (int index in newTxHashesIndexes.AsSpan())
        {
            ref readonly ValueHash256 hash = ref valueHashes[start + index];
            (int Size, TxType Type) txShape = TryGetTxShape(in hash, out (int Size, TxType Type) announcedShape)
                ? announcedShape
                : (sizes[index], (TxType)types[index]);
            int txSize = txShape.Size;

            bool isOversized = txSize > TransactionsMessage.MaxPacketSize;
            if (ShouldSendCurrentRequest(txSize, packetSizeLeft, responseSizeLeft, toRequestCount, hasOversizedTransaction))
            {
                SendHashesToRequest();
            }

            hashesToRequest ??= new ArrayPoolList<ValueHash256>(requestCapacity);
            hashesToRequest.Add(hash);
            toRequestCount++;

            if (isOversized)
            {
                responseSizeLeft -= txSize;
                hasOversizedTransaction = true;
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
            ArrayPoolList<ValueHash256> request = hashesToRequest!;
            hashesToRequest = null;
            SendPooledTransactionRequest<V66.Messages.GetPooledTransactionsMessage>(request);
            packetSizeLeft = TransactionsMessage.MaxPacketSize;
            responseSizeLeft = responseSizeLimit;
            toRequestCount = 0;
            hasOversizedTransaction = false;
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
        ArrayPoolList<ValueHash256>? hashesWithShape = null;
        ArrayPoolList<int>? sizes = null;
        ArrayPoolList<byte>? types = null;
        ArrayPoolList<ValueHash256>? hashesWithoutShape = null;

        try
        {
            for (int i = 0; i < txHashes.Length; i++)
            {
                ValueHash256 txHash = txHashes[i];
                if (TryGetTxShape(in txHash, out (int Size, TxType Type) txShape))
                {
                    hashesWithShape ??= new ArrayPoolList<ValueHash256>(txHashes.Length);
                    sizes ??= new ArrayPoolList<int>(txHashes.Length);
                    types ??= new ArrayPoolList<byte>(txHashes.Length);

                    hashesWithShape.Add(txHash);
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

    private protected bool CanRequestPooledTransaction(TxType txType, ref bool? frameTxsEnabled) =>
        CanDecodeTransactionType(txType)
        && (txType is not TxType.Blob || _blobSupportEnabled)
        && (txType is not TxType.FrameTx || (frameTxsEnabled ??= FrameTxsEnabled()));

    private static bool CanDecodeTransactionType(TxType txType) => txType switch
    {
        TxType.Legacy or TxType.AccessList or TxType.EIP1559 or TxType.Blob or TxType.SetCode or TxType.FrameTx => true,
        _ => false,
    };

    /// <remarks>Reads the same head spec as the ingress filter, so the gate opens exactly when the pool starts accepting frame txs.</remarks>
    private bool FrameTxsEnabled() => specProvider.GetCurrentHeadSpec().IsEip8141Enabled;

    /// <remarks>An announcement cannot tell a blob-carrying type-6 from a blobless one, so it is admitted up to
    /// the larger of the two SizeTxFilter caps; either alone would drop valid announcements of the other kind.</remarks>
    private protected long MaxAnnouncedSize(TxType txType) =>
        txType.SupportsBlobs()
            ? _configuredMaxBlobTxSize
            : txType is TxType.FrameTx && _blobSupportEnabled
                ? Math.Max(_configuredMaxTxSize, _configuredMaxBlobTxSize)
                : _configuredMaxTxSize;

    private static bool ShouldSendCurrentRequest(
        int txSize,
        int packetSizeLeft,
        int responseSizeLeft,
        int toRequestCount,
        bool hasOversizedTransaction)
    {
        if (toRequestCount == 0)
        {
            return false;
        }

        bool isOversized = txSize > TransactionsMessage.MaxPacketSize;
        return isOversized != hasOversizedTransaction
            || (isOversized ? txSize > responseSizeLeft : txSize > packetSizeLeft);
    }

    private ArrayPoolListRef<int> AddMarkUnknownHashes(
        IOwnedReadOnlyList<ValueHash256> hashes,
        int start,
        ReadOnlySpan<int> sizes,
        ReadOnlySpan<byte> types,
        bool registerForRetry)
    {
        // Resolved on the first type-6 byte, so a batch without one does no head lookup.
        bool? frameTxsEnabled = null;

        ArrayPoolListRef<int> discoveredTxHashesAndSizes = new(sizes.Length);
        ReadOnlySpan<ValueHash256> valueHashes = hashes.AsSpan();
        for (int i = 0; i < sizes.Length; i++)
        {
            ref readonly ValueHash256 hash = ref valueHashes[start + i];
            bool isKnown = _txPool.IsKnown(in hash);
            if (!isKnown)
            {
                (int Size, TxType Type) txShape = (sizes[i], (TxType)types[i]);
                if (txShape.Size <= 0 || !CanDecodeTransactionType(txShape.Type))
                {
                    continue;
                }

                // Delivered transactions must match their first decodable announcement even when that announcement was not requestable.
                if (!TryGetTxShape(in hash, out (int Size, TxType Type) retainedShape))
                {
                    SetTxShape(in hash, txShape);
                    retainedShape = txShape;
                }

                if (!CanRequestPooledTransaction(retainedShape.Type, ref frameTxsEnabled)
                    || retainedShape.Size > MaxAnnouncedSize(retainedShape.Type))
                {
                    // Keep the valid shape for delivery validation, but make shaped retries a no-op for transactions this node cannot process.
                    continue;
                }

                if (!registerForRetry || _txPool.NotifyAboutTx(in hash, this) is AnnounceResult.RequestRequired)
                {
                    discoveredTxHashesAndSizes.Add(i);
                }
            }
        }

        return discoveredTxHashesAndSizes;
    }

    protected override bool ShouldNotifyTransactionCore(Transaction tx) =>
        !IsSparseBlobTransaction(tx) && base.ShouldNotifyTransactionCore(tx);

    protected override void SendNewTransactionCore(Transaction tx)
    {
        if (tx.CanBeBroadcast())
        {
            base.SendNewTransactionCore(tx);
        }
        else
        {
            NewPooledTransactionHashesMessageSerializer.PooledMessage message = NewPooledTransactionHashesMessageSerializer.PooledMessage.Rent();
            Add(message, tx);
            SendAnnouncement(message);
        }
    }

    protected override void SendNewTransactionsCore(ReadOnlySpan<Transaction> txs, bool sendFullTx)
    {
        if (sendFullTx)
        {
            base.SendNewTransactionsCore(txs, sendFullTx);
            return;
        }

        NewPooledTransactionHashesMessageSerializer.PooledMessage? message = null;
        foreach (Transaction tx in txs)
        {
            if (tx.Hash is null) continue;

            message ??= NewPooledTransactionHashesMessageSerializer.PooledMessage.Rent();
            Add(message, tx);
            TxPool.Metrics.PendingTransactionsHashesSent++;
            if (message.HashList.Count == NewPooledTransactionHashesMessage68.MaxCount)
            {
                SendAnnouncement(message);
                message = null;
            }
        }

        if (message is not null)
        {
            SendAnnouncement(message);
        }
    }

    private static void Add(NewPooledTransactionHashesMessageSerializer.PooledMessage message, Transaction tx)
    {
        message.TypeList.Add((byte)tx.Type);
        message.SizeList.Add(tx.GetLength());
        message.HashList.Add(tx.Hash!.ValueHash256);
    }

    // The serializer is looked up by the static message type, which must not be the pooled subclass.
    private void SendAnnouncement(NewPooledTransactionHashesMessage68 message) => Send(message);

    protected override bool CanServePooledTransaction(Transaction tx) => !IsSparseBlobTransaction(tx);

    /// <inheritdoc/>
    protected override bool TryGetPooledTransactionToServe(in ValueHash256 hash, [NotNullWhen(true)] out Transaction? tx)
    {
        if (_txPool.TryGetPendingBlobCellMask(hash, out BlobCellMask availableMask) && !availableMask.IsFull)
        {
            tx = default;
            return false;
        }

        return base.TryGetPooledTransactionToServe(hash, out tx);
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
                ReturnUnsubmittedTransactions(transactionsSpan[startIdx..]);
                transactions.Dispose();
                throw new SubprotocolException("invalid pooled tx type or size");
            }
        }

        return base.HandleSlow(request, cancellationToken);
    }

    /// <remarks>
    /// Unlike <see cref="ValidateSizeAndType"/>, a matching announcement is not taken: it still describes this hash
    /// correctly, and the bounded cache evicts it.
    /// </remarks>
    private protected override bool MatchesAnnouncedShape(in ValueHash256 hash, TxType type, int size) =>
        !TryGetTxShape(in hash, out (int Size, TxType Type) txShape)
        || txShape.Size == size && txShape.Type == type;

    private bool ValidateSizeAndType(Transaction? tx)
        => tx is not null
        && !IsSparseBlobTransaction(tx)
        && (!TryTakeTxShape(tx.Hash!, out (int Size, TxType Type) txShape) || (MatchesAnnouncedTransactionSize(tx, txShape.Size) && tx.Type == txShape.Type));

    /// <summary>
    /// Checks a fetched pooled transaction against the size its announcement declared.
    /// </summary>
    protected static bool MatchesAnnouncedSize(Transaction tx, int announcedSize)
    {
        int actualSize = tx.GetLength();
        return actualSize == announcedSize
            || tx.SupportsBlobs
            && tx.NetworkWrapper is ShardBlobNetworkWrapper wrapper
            && GetGethBlobTransactionSizeEstimate(tx, wrapper) == announcedSize;
    }

    // geth estimates the network wrapper as the canonical transaction plus a separately wrapped sidecar.
    // https://github.com/ethereum/go-ethereum/blob/fdce1ff22f2c2bde0e6a8d1921168f4b7036781f/core/txpool/blobpool/blobpool.go#L174-L201
    private static int GetGethBlobTransactionSizeEstimate(Transaction tx, ShardBlobNetworkWrapper wrapper)
    {
        int sidecarContentLength = checked(
            Rlp.LengthOf(wrapper.Blobs)
            + Rlp.LengthOf(wrapper.Commitments)
            + Rlp.LengthOf(wrapper.Proofs));
        return checked(tx.GetLength(shouldCountBlobs: false) + Rlp.LengthOfSequence(sidecarContentLength));
    }

    protected virtual bool MatchesAnnouncedTransactionSize(Transaction tx, int announcedSize) =>
        MatchesAnnouncedSize(tx, announcedSize);

    private static bool IsSparseBlobTransaction(Transaction tx) =>
        tx is LightTransaction { ProofVersion: ProofVersion.V1 } lightTx && !lightTx.BlobCellMask.IsFull
        || tx.NetworkWrapper is ShardBlobNetworkWrapper { Version: ProofVersion.V1 } wrapper && !wrapper.HasFullBlobs();
}
