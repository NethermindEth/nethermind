// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V72;
using Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73;

/// <summary>
/// Implements eth/73 (EIP-8077): eth/72 with transaction announcements that also carry each transaction's source address and nonce.
/// </summary>
/// <remarks>
/// The source is the account whose nonce the transaction consumes: the recovered signer, or the explicit sender of an
/// EIP-8141 frame transaction. EIP-8077 leaves the use of the received metadata for fetch scheduling to implementations,
/// so announcements are fetched as in eth/72; a requested transaction that contradicts its announced source or nonce is
/// treated as a protocol violation. A sparse blob transaction is submitted by the sparse blob registry once its cells are
/// assembled, so its recovered sender is not checked.
/// </remarks>
public class Eth73ProtocolHandler(
    ISession session,
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
    IBlobCustodyTracker blobCustodyTracker,
    ISparseBlobPoolPeerRegistry sparseBlobPoolPeerRegistry,
    ITxGossipPolicy? transactionsGossipPolicy = null)
    : Eth72ProtocolHandler(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, forkInfo, logManager, txPoolConfig, specProvider, blobCustodyTracker, sparseBlobPoolPeerRegistry, transactionsGossipPolicy), IStaticProtocolInfo
{
    private const string AnnouncementMismatch = "pooled tx does not match its announced source or nonce";

    private readonly ClockCache<ValueHash256, (Address Source, ulong Nonce)> _announcedSourcesAndNonces = new(MemoryAllowance.TxHashCacheSize / 10, lockPartition: 1);

    public override string Name => "eth73";

    public new static byte Version => EthVersions.Eth73;
    public override byte ProtocolVersion => Version;

    private protected override NewPooledTransactionHashesMessage72 DeserializeNewPooledTransactionHashes(IByteBuffer content) =>
        Deserialize<NewPooledTransactionHashesMessage73>(content);

    private protected override void OnPooledTransactionRequested(NewPooledTransactionHashesMessage72 message, int index)
    {
        NewPooledTransactionHashesMessage73 announcement = (NewPooledTransactionHashesMessage73)message;
        _announcedSourcesAndNonces.Set(announcement.Hashes[index], (announcement.Sources[index], announcement.Nonces[index]));
    }

    private protected override void SendAnnouncement(IReadOnlyList<Transaction> txs, byte[] cellMask)
    {
        int count = txs.Count;
        ArrayPoolList<byte> types = new(count);
        ArrayPoolList<int> sizes = new(count);
        ArrayPoolList<ValueHash256> hashes = new(count);
        ArrayPoolList<Address> sources = new(count);
        ArrayPoolList<ulong> nonces = new(count);

        AddAnnouncedTransactions(txs, types, sizes, hashes, sources, nonces);

        if (hashes.Count != 0)
        {
            Send(new NewPooledTransactionHashesMessage73(types, sizes, hashes, cellMask, sources, nonces));
        }
        else
        {
            types.Dispose();
            sizes.Dispose();
            hashes.Dispose();
            sources.Dispose();
            nonces.Dispose();
        }
    }

    /// <remarks>
    /// Checks the announced nonce, and the announced source when the sender is already known (the explicit sender of a
    /// frame transaction), before the pool sees the batch. A recovered sender is checked in
    /// <see cref="OnTransactionSubmitted"/> instead, once the pool has resolved it, so a transaction the pool rejects
    /// before sender recovery costs no signature recovery here.
    /// </remarks>
    protected override ValueTask HandleSlow(TransactionsRequest request, CancellationToken cancellationToken)
    {
        IOwnedReadOnlyList<Transaction> transactions = request.Transactions;
        ReadOnlySpan<Transaction> transactionsSpan = transactions.AsSpan();
        int startIdx = request.StartIndex;
        for (int i = startIdx; i < transactionsSpan.Length; i++)
        {
            if (!MatchesAnnouncedNonceAndKnownSource(transactionsSpan[i]))
            {
                // [0, startIdx) were already handled in a prior scheduler slot.
                ReturnUnsubmittedTransactions(transactionsSpan[startIdx..]);
                transactions.Dispose();
                Session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, AnnouncementMismatch);
                return ValueTask.CompletedTask;
            }
        }

        return base.HandleSlow(request, cancellationToken);
    }

    private protected override void OnTransactionSubmitted(Transaction tx)
    {
        if (tx.Hash is not null
            && _announcedSourcesAndNonces.Delete(tx.Hash.ValueHash256, out (Address Source, ulong Nonce) announced)
            && tx.SenderAddress is not null
            && tx.SenderAddress != announced.Source)
        {
            Session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, AnnouncementMismatch);
        }
    }

    private bool MatchesAnnouncedNonceAndKnownSource(Transaction? tx)
    {
        if (tx?.Hash is null || !_announcedSourcesAndNonces.TryGet(tx.Hash.ValueHash256, out (Address Source, ulong Nonce) announced))
        {
            return true;
        }

        if (tx.Nonce != announced.Nonce)
        {
            return false;
        }

        if (tx.SenderAddress is null)
        {
            return true;
        }

        _announcedSourcesAndNonces.Delete(tx.Hash.ValueHash256);
        return tx.SenderAddress == announced.Source;
    }
}
