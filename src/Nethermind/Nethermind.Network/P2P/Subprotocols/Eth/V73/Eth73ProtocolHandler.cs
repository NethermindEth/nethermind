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

/// <summary>eth/73 (EIP-8077): eth/72 with each announced transaction's source and nonce.</summary>
/// <remarks>
/// A requested transaction contradicting its announcement is a protocol breach. A sender not yet recovered on arrival is
/// checked once the pool, or for a sparse blob transaction the sampling validation, recovers it.
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

    protected override NewPooledTransactionHashesMessage72 DeserializeNewPooledTransactionHashes(IByteBuffer content) =>
        Deserialize<NewPooledTransactionHashesMessage73>(content);

    protected override void OnPooledTransactionRequested(NewPooledTransactionHashesMessage72 message, int index)
    {
        NewPooledTransactionHashesMessage73 announcement = (NewPooledTransactionHashesMessage73)message;
        _announcedSourcesAndNonces.Set(announcement.Hashes[index], (announcement.Sources[index], announcement.Nonces[index]));
    }

    protected override void SendAnnouncement(IReadOnlyList<Transaction> txs, byte[] cellMask)
    {
        int count = txs.Count;
        ArrayPoolList<byte> types = new(count);
        ArrayPoolList<int> sizes = new(count);
        ArrayPoolList<ValueHash256> hashes = new(count);
        ArrayPoolList<Address> sources = new(count);
        ArrayPoolList<ulong> nonces = new(count);
        NewPooledTransactionHashesMessage73 message = new(types, sizes, hashes, cellMask, sources, nonces);
        bool isTransferred = false;
        try
        {
            AddAnnouncedTransactions(txs, types, sizes, hashes, sources, nonces);
            if (hashes.Count != 0)
            {
                isTransferred = true;
                Send(message);
            }
        }
        finally
        {
            if (!isTransferred) message.Dispose();
        }
    }

    /// <remarks>
    /// Recovered senders are checked in <see cref="OnTransactionSubmitted"/>, so transactions the pool rejects early skip recovery.
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

    protected override void OnTransactionSubmitted(Transaction tx)
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
