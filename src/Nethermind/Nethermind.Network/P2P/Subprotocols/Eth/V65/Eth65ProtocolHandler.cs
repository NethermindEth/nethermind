// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Contract.Messages;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Utils;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V64;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using System;
using Microsoft.Extensions.ObjectPool;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
        ITxGossipPolicy? transactionsGossipPolicy = null,
        TimeProvider? timeProvider = null)
        : Eth64ProtocolHandler(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, forkInfo, logManager, transactionsGossipPolicy, timeProvider),
          IBatchMessageHandler<PooledTransactionRequestMessage, ValueHash256>
    {
        public override string Name => "eth65";

        public override byte ProtocolVersion => EthVersions.Eth65;

        private const int MaxNumberOfTxsInOneMsg = 256;
        private LegacyResponseCredits? _legacyResponseCredits;
        private static readonly ObjectPool<HashSet<ValueHash256>> ResponseHashesPool =
            new DefaultObjectPool<HashSet<ValueHash256>>(new ResponseHashesPolicy(), maximumRetained: 16);

        private sealed class ResponseHashesPolicy : PooledObjectPolicy<HashSet<ValueHash256>>
        {
            public override HashSet<ValueHash256> Create() => [with(MaxNumberOfTxsInOneMsg)];

            public override bool Return(HashSet<ValueHash256> hashes)
            {
                hashes.Clear();
                return true;
            }
        }
        private static readonly int PooledTransactionsResponseSoftLimit = (int)2.MiB;

        protected override bool HandleMessageCore(ZeroPacket message)
        {
            int size = message.Content.Length;
            switch (message.PacketType)
            {
                case Eth65MessageCode.NewPooledTransactionHashes:
                    if (CanReceiveTransactions)
                    {
                        if (IsTransactionGossipAllowed())
                        {
                            using NewPooledTransactionHashesMessage newPooledTxMsg = Deserialize<NewPooledTransactionHashesMessage>(message);
                            ReportIn(newPooledTxMsg, size);
                            Handle(newPooledTxMsg);
                        }
                        else
                        {
                            const string txFlooding = $"Ignoring {nameof(NewPooledTransactionHashesMessage)} because of transaction flooding.";
                            ReportIn(txFlooding, size);
                        }
                    }
                    else
                    {
                        const string ignored = $"{nameof(NewPooledTransactionHashesMessage)} ignored, syncing";
                        ReportIn(ignored, size);
                    }

                    return true;
                case Eth65MessageCode.GetPooledTransactions:
                    HandleServingInBackground<GetPooledTransactionsMessage>(message, Handle);
                    return true;
                case Eth65MessageCode.PooledTransactions:
                    if (CanReceiveTransactions)
                    {
                        if (!TryDeserializeTransactions(message, out PooledTransactionsMessage pooledTxMsg, static txMessage => txMessage, pooledResponse: true))
                        {
                            _legacyResponseCredits?.Consume();
                            return true;
                        }
                        PeerWorkQuota.Reservation? replyReservation = null;
                        PooledTransactionRequests.ResponseContext? context = null;
                        bool solicited = _legacyResponseCredits?.TryClaim(pooledTxMsg.Transactions.AsSpan(), out replyReservation, out context) == true;
                        using PeerWorkQuota.Reservation? reservation = replyReservation;
                        ReportIn(pooledTxMsg, size);
                        HandlePooledTransactions(pooledTxMsg, solicited, reservation, context);
                    }
                    else
                    {
                        IgnorePooledTransactionResponse();
                        const string ignored = $"{nameof(PooledTransactionsMessage)} ignored, syncing";
                        ReportIn(ignored, size);
                    }

                    return true;
                default:
                    return base.HandleMessageCore(message);
            }
        }

        protected virtual void Handle(NewPooledTransactionHashesMessage msg) => RequestPooledTransactions<GetPooledTransactionsMessage>(msg.Hashes);

        protected void AddNotifiedTransactions(ReadOnlySpan<ValueHash256> hashes)
        {
            for (int i = 0; i < hashes.Length; i++)
            {
                NotifiedTransactions.Set(hashes[i]);
            }
        }

        private async ValueTask Handle(GetPooledTransactionsMessage msg, CancellationToken cancellationToken)
        {
            using GetPooledTransactionsMessage message = msg;
            long startTime = Stopwatch.GetTimestamp();
            Send(await FulfillPooledTransactionsRequest(message, cancellationToken));
            if (Logger.IsTrace)
                Logger.Trace($"OUT {Counter:D5} {nameof(GetPooledTransactionsMessage)} to {Node?.ToString("c"):hide} " +
                             $"in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");
        }

        internal ValueTask<PooledTransactionsMessage> FulfillPooledTransactionsRequest(GetPooledTransactionsMessage msg, CancellationToken cancellationToken)
        {
            int maxResponseCount = Math.Min(msg.Hashes.Count, MaxNumberOfTxsInOneMsg);
            ArrayPoolList<Transaction> txsToSend = [with(maxResponseCount)];
            ArrayPoolList<PooledTransactionsMessage.EncodedTransaction>? encodedTxsToSend = null;

            // Once a response is non-empty, enforce the eth spec's 256-hash soft limit. If the
            // first 256 hashes miss, keep scanning without growing the deduplication set.
            HashSet<ValueHash256> seenHashes = ResponseHashesPool.Get();
            try
            {
                // Eth/68 and later use the 2 MiB pooled-transactions soft response limit from the devp2p eth capability.
                int packetSizeLeft = ProtocolVersion >= EthVersions.Eth68
                    ? PooledTransactionsResponseSoftLimit
                    : TransactionsMessage.MaxPacketSize;
                int responseCount = 0;
                foreach (ref readonly ValueHash256 hash in msg.Hashes.AsSpan())
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    if (seenHashes.Count >= MaxNumberOfTxsInOneMsg && responseCount > 0)
                    {
                        break;
                    }

                    if (seenHashes.Count < MaxNumberOfTxsInOneMsg && !seenHashes.Add(hash))
                    {
                        continue;
                    }

                    int txSize;
                    Transaction? responseTx = null;
                    if (TryGetEncodedPooledTransactionToServe(hash, out IOwnedReadOnlyList<byte>? encodedTx, out bool unservable))
                    {
                        txSize = encodedTx.Count;
                    }
                    else if (!unservable && TryGetPooledTransactionToServe(hash, out Transaction tx) && CanServePooledTransaction(tx))
                    {
                        responseTx = PreparePooledTransactionForResponse(tx);
                        txSize = responseTx.GetLength();
                    }
                    else
                    {
                        continue;
                    }

                    if (txSize > packetSizeLeft && responseCount > 0)
                    {
                        encodedTx?.Dispose();
                        break;
                    }

                    if (encodedTx is null)
                    {
                        txsToSend.Add(responseTx!);
                    }
                    else
                    {
                        (encodedTxsToSend ??= [with(maxResponseCount)]).Add(new(txsToSend.Count, encodedTx));
                    }

                    responseCount++;
                    packetSizeLeft -= txSize;
                    TxPool.Metrics.PendingTransactionsSent++;
                }

                PooledTransactionsMessage response = CreatePooledTransactionsMessage(txsToSend);
                response.EncodedTransactions = encodedTxsToSend;
                return new ValueTask<PooledTransactionsMessage>(response);
            }
            catch
            {
                txsToSend.Dispose();
                PooledTransactionsMessage.DisposeEncodedTransactions(encodedTxsToSend);
                throw;
            }
            finally
            {
                ResponseHashesPool.Return(seenHashes);
            }
        }

        /// <summary>Whether a pooled transaction that <see cref="TryGetPooledTransactionToServe"/> gives is served.</summary>
        /// <remarks>
        /// Not called for a transaction that <see cref="TryGetEncodedPooledTransactionToServe"/> serves. An override that
        /// rejects such a transaction must also override that method.
        /// </remarks>
        protected virtual bool CanServePooledTransaction(Transaction tx) => true;

        /// <summary>
        /// Gets the representation of a pooled transaction that this protocol version can serve.
        /// </summary>
        protected virtual bool TryGetPooledTransactionToServe(in ValueHash256 hash, [NotNullWhen(true)] out Transaction? tx)
            => _txPool.TryGetPendingTransaction(hash, out tx);

        /// <summary>Gets the network encoding of a pooled transaction that is served without decoding it.</summary>
        /// <remarks>
        /// Tried before <see cref="TryGetPooledTransactionToServe"/>, which serves the transaction when this misses and
        /// <paramref name="unservable"/> is <see langword="false"/>. The encoding is served as it is:
        /// <see cref="CanServePooledTransaction"/> and <see cref="PreparePooledTransactionForResponse"/> do not see it, so
        /// a version that overrides either of them must also override this method. This version serves no encodings.
        /// </remarks>
        /// <param name="encodedTx">The encoding without the typed envelope. The caller disposes it.</param>
        /// <param name="unservable">Whether the transaction is not served at all, so it is not looked up again.</param>
        private protected virtual bool TryGetEncodedPooledTransactionToServe(in ValueHash256 hash, [NotNullWhen(true)] out IOwnedReadOnlyList<byte>? encodedTx, out bool unservable)
        {
            encodedTx = null;
            unservable = false;
            return false;
        }

        /// <summary>Gets the transaction that the response carries for a pooled transaction.</summary>
        /// <remarks>
        /// Not called for a transaction that <see cref="TryGetEncodedPooledTransactionToServe"/> serves. An override that
        /// changes such a transaction must also override that method.
        /// </remarks>
        protected virtual Transaction PreparePooledTransactionForResponse(Transaction tx) => tx;

        /// <summary>Builds the response to a <see cref="GetPooledTransactionsMessage"/>.</summary>
        /// <remarks>Overridden by protocol versions that carry the response on a different message code.</remarks>
        protected virtual PooledTransactionsMessage CreatePooledTransactionsMessage(IOwnedReadOnlyList<Transaction> transactions) => new(transactions);

        /// <summary>Builds a transaction hash announcement.</summary>
        /// <inheritdoc cref="CreatePooledTransactionsMessage" path="/remarks"/>
        protected virtual NewPooledTransactionHashesMessage CreateAnnouncementMessage(IOwnedReadOnlyList<Hash256> hashes) => new(hashes);

        protected override void SendNewTransactionsCore(ReadOnlySpan<Transaction> txs, bool sendFullTx)
        {
            void SendNewPooledTransactionMessage(IOwnedReadOnlyList<Hash256> hashes) => Send(CreateAnnouncementMessage(hashes));

            if (sendFullTx)
            {
                base.SendNewTransactionsCore(txs, true);
                return;
            }

            ArrayPoolList<Hash256> hashes = [with(NewPooledTransactionHashesMessage.MaxCount)];

            foreach (Transaction tx in txs)
            {
                if (hashes.Count == NewPooledTransactionHashesMessage.MaxCount)
                {
                    SendNewPooledTransactionMessage(hashes);
                    hashes = [with(NewPooledTransactionHashesMessage.MaxCount)];
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

        private protected bool SendPooledTransactionRequest<TMessage>(IOwnedReadOnlyList<ValueHash256> hashes)
            where TMessage : P2PMessage, INew<IOwnedReadOnlyList<ValueHash256>, TMessage>
        {
            TMessage message = TMessage.New(hashes);
            if (!BackgroundTaskScheduler.TryReservePooledReply(out PeerWorkQuota.Reservation? replyReservation))
            {
                ExpirePooledTransactionRequests();
                if (!BackgroundTaskScheduler.TryReservePooledReply(out replyReservation))
                {
                    DeferUnsentPooledRequests(hashes.AsSpan());
                    message.Dispose();
                    return false;
                }
            }
            using PeerWorkQuota.Reservation replyLease = replyReservation;
            using ArrayPoolListRef<ValueHash256> retryHashes = new(hashes.AsSpan());
            if (message is not GetPooledTransactionsMessage)
            {
                if (!TryTrackPooledTransactionRequest(message, replyLease, out RetryRequestConfirmation? confirmation))
                {
                    CompleteRequest(confirmation, false);
                    DeferUnsentPooledRequests(hashes.AsSpan());
                    message.Dispose();
                    return false;
                }
                bool modernSent = false;
                try
                {
                    ReportPooledTransactionRequest(hashes.AsSpan());
                    modernSent = SendAndGetSize(message) > 0 && !Session.IsClosing;
                    if (modernSent) ConfirmPooledTransactionRequestSent(message);
                }
                finally
                {
                    CompleteRequest(confirmation, modernSent);
                    if (!modernSent)
                    {
                        CancelPooledTransactionRequest(message);
                        DeferUnsentPooledRequests(retryHashes.AsSpan());
                        IgnorePooledTransactionResponse();
                    }
                }
                return modernSent;
            }

            LegacyResponseCredits credits = LegacyCredits;
            using ArrayPoolList<RetryRequestToken<ValueHash256>> tokens = CaptureRequestTokens(hashes.AsSpan());
            RetryRequestConfirmation? legacyConfirmation = GetRequestConfirmation(tokens);
            LinkedListNode<long>? reservation = Session.IsClosing ? null : credits.Reserve(replyLease, hashes.AsSpan(), tokens.AsSpan());
            if (reservation is null)
            {
                CompleteRequest(legacyConfirmation, false);
                DeferUnsentPooledRequests(hashes.AsSpan());
                message.Dispose();
                return false;
            }
            bool sent = false;
            try
            {
                ReportPooledTransactionRequest(hashes.AsSpan());
                sent = SendAndGetSize(message) > 0 && !Session.IsClosing;
            }
            finally
            {
                CompleteRequest(legacyConfirmation, sent);
                if (!sent)
                {
                    credits.Cancel(reservation);
                    DeferUnsentPooledRequests(retryHashes.AsSpan());
                    IgnorePooledTransactionResponse();
                }
            }
            return sent;
        }

        private void DeferUnsentPooledRequests(ReadOnlySpan<ValueHash256> hashes)
        {
            if (Session.IsClosing) return;
            foreach (ref readonly ValueHash256 hash in hashes) _txPool.TryDeferTransactionRequest(in hash, this);
        }

        private protected static bool MatchesPooledTransactionRequest(ReadOnlySpan<Transaction> transactions, ReadOnlySpan<ValueHash256> requestedHashes)
        {
            int requestedIndex = 0;
            for (int i = 0; i < transactions.Length; i++)
            {
                Hash256? hash = transactions[i].Hash;
                if (hash is null)
                {
                    return false;
                }

                while (requestedIndex < requestedHashes.Length
                    && requestedHashes[requestedIndex] != hash.ValueHash256)
                {
                    requestedIndex++;
                }

                if (requestedIndex == requestedHashes.Length)
                {
                    return false;
                }

                requestedIndex++;
            }

            return true;
        }

        private protected virtual void ExpirePooledTransactionRequests() => Volatile.Read(ref _legacyResponseCredits)?.ExpirePending();

        private protected virtual bool TryTrackPooledTransactionRequest(P2PMessage message, PeerWorkQuota.Reservation reservation,
            out RetryRequestConfirmation? confirmation)
        {
            confirmation = null;
            return false;
        }

        private protected virtual void ConfirmPooledTransactionRequestSent(P2PMessage message) { }

        private protected virtual void CancelPooledTransactionRequest(P2PMessage message) { }

        private LegacyResponseCredits LegacyCredits => LazyInitializer.EnsureInitialized(ref _legacyResponseCredits,
            () => new(BackgroundTaskScheduler.TimeProvider));

        protected override void OnDisposed()
        {
            LegacyCredits.Dispose();
            base.OnDisposed();
        }

        protected void RequestPooledTransactions<TMessage>(IOwnedReadOnlyList<Hash256> hashes, bool registerForRetry = true)
            where TMessage : P2PMessage, INew<IOwnedReadOnlyList<ValueHash256>, TMessage>
        {
            using ArrayPoolListRef<ValueHash256> values = new(hashes.Count);
            foreach (Hash256 hash in hashes.AsSpan()) values.Add(hash.ValueHash256);
            RequestPooledTransactions<TMessage>(values.AsSpan(), registerForRetry);
        }

        private void RequestPooledTransactions<TMessage>(ReadOnlySpan<ValueHash256> hashesSpan, bool registerForRetry)
            where TMessage : P2PMessage, INew<IOwnedReadOnlyList<ValueHash256>, TMessage>
        {
            foreach (ref readonly ValueHash256 hash in hashesSpan) NotifiedTransactions.Set(in hash);

            long startTime = Stopwatch.GetTimestamp();
            TxPool.Metrics.PendingTransactionsHashesReceived += hashesSpan.Length;

            for (int start = 0; start < hashesSpan.Length; start += MaxNumberOfTxsInOneMsg)
            {
                int count = Math.Min(MaxNumberOfTxsInOneMsg, hashesSpan.Length - start);
                ArrayPoolList<ValueHash256> newTxHashes = AddMarkUnknownHashes(hashesSpan.Slice(start, count), registerForRetry);
                if (newTxHashes.Count is 0)
                {
                    newTxHashes.Dispose();
                }
                else
                {
                    SendPooledTransactionRequest<TMessage>(newTxHashes);
                }
            }

            if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} {nameof(NewPooledTransactionHashesMessage)} to {Node?.ToString("c"):hide} " +
                                             $"in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");
        }

        private ArrayPoolList<ValueHash256> AddMarkUnknownHashes(ReadOnlySpan<ValueHash256> hashes, bool registerForRetry)
        {
            ArrayPoolList<ValueHash256> discoveredTxHashesAndSizes = [with(hashes.Length)];

            for (int i = 0; i < hashes.Length; i++)
            {
                ref readonly ValueHash256 hash = ref hashes[i];
                if (!_txPool.IsKnown(in hash)
                    && (!registerForRetry || _txPool.NotifyAboutTx(in hash, this) is AnnounceResult.RequestRequired))
                {
                    discoveredTxHashesAndSizes.Add(hash);
                }
            }

            return discoveredTxHashesAndSizes;
        }

        public virtual void HandleMessage(PooledTransactionRequestMessage message) =>
            HandleMessages<GetPooledTransactionsMessage>([message.TxHash]);

        public virtual void HandleMessages(ReadOnlySpan<ValueHash256> txHashes) =>
            HandleMessages<GetPooledTransactionsMessage>(txHashes);

        protected void HandleMessages<TMessage>(ReadOnlySpan<ValueHash256> txHashes)
            where TMessage : P2PMessage, INew<IOwnedReadOnlyList<ValueHash256>, TMessage>
        {
            for (int start = 0; start < txHashes.Length; start += MaxNumberOfTxsInOneMsg)
            {
                int count = Math.Min(MaxNumberOfTxsInOneMsg, txHashes.Length - start);
                RequestPooledTransactions<TMessage>(txHashes.Slice(start, count), registerForRetry: false);
            }
        }
    }
}
