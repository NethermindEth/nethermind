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
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V72;
using Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73;

/// <summary>
/// Implements eth/73 (EIP-8077): eth/72 with transaction announcements that also carry each transaction's source address and nonce.
/// </summary>
/// <remarks>
/// The source is the account whose nonce the transaction consumes: the recovered signer, or the explicit sender of an
/// EIP-8141 frame transaction. EIP-8077 leaves the use of the received metadata for fetch scheduling to implementations,
/// so announcements are fetched as in eth/72; a delivered transaction that contradicts its announced source or nonce is
/// treated as a protocol violation.
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
    IEthereumEcdsa ecdsa,
    ITxGossipPolicy? transactionsGossipPolicy = null)
    : Eth72ProtocolHandler(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, forkInfo, logManager, txPoolConfig, specProvider, blobCustodyTracker, sparseBlobPoolPeerRegistry, transactionsGossipPolicy), IStaticProtocolInfo
{
    private readonly ClockCache<ValueHash256, (Address Source, ulong Nonce)> _announcedSourcesAndNonces = new(MemoryAllowance.TxHashCacheSize / 10, lockPartition: 1);

    public override string Name => "eth73";

    public new static byte Version => EthVersions.Eth73;
    public override byte ProtocolVersion => Version;

    private protected override NewPooledTransactionHashesMessage72 DeserializeNewPooledTransactionHashes(IByteBuffer content)
    {
        NewPooledTransactionHashesMessage73 message = Deserialize<NewPooledTransactionHashesMessage73>(content);
        ReadOnlySpan<ValueHash256> hashes = message.Hashes.AsSpan();
        ReadOnlySpan<Address> sources = message.Sources.AsSpan();
        ReadOnlySpan<ulong> nonces = message.Nonces.AsSpan();
        for (int i = 0; i < hashes.Length; i++)
        {
            if (!_txPool.IsKnown(in hashes[i]))
            {
                _announcedSourcesAndNonces.Set(hashes[i], (sources[i], nonces[i]));
            }
        }

        return message;
    }

    private protected override void SendAnnouncement(IReadOnlyList<Transaction> txs, byte[] cellMask)
    {
        int count = txs.Count;
        ArrayPoolList<byte> types = new(count);
        ArrayPoolList<int> sizes = new(count);
        ArrayPoolList<ValueHash256> hashes = new(count);
        ArrayPoolList<Address> sources = new(count);
        ArrayPoolList<ulong> nonces = new(count);

        for (int i = 0; i < count; i++)
        {
            Transaction tx = txs[i];
            int announcementSize = GetAnnouncementSize(tx);
            if (announcementSize <= 0 || tx.SenderAddress is not { } source)
            {
                continue;
            }

            types.Add((byte)tx.Type);
            sizes.Add(announcementSize);
            hashes.Add(tx.Hash!.ValueHash256);
            sources.Add(source);
            nonces.Add(tx.Nonce);
            TxPool.Metrics.PendingTransactionsHashesSent++;
        }

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

    protected override ValueTask HandleSlow(TransactionsRequest request, CancellationToken cancellationToken)
    {
        IOwnedReadOnlyList<Transaction> transactions = request.Transactions;
        ReadOnlySpan<Transaction> transactionsSpan = transactions.AsSpan();
        int startIdx = request.StartIndex;
        for (int i = startIdx; i < transactionsSpan.Length; i++)
        {
            if (!MatchesAnnouncedSourceAndNonce(transactionsSpan[i]))
            {
                // [0, startIdx) were already handled in a prior scheduler slot.
                ReturnUnsubmittedTransactions(transactionsSpan[startIdx..]);
                transactions.Dispose();
                throw new SubprotocolException("pooled tx does not match its announced source or nonce");
            }
        }

        return base.HandleSlow(request, cancellationToken);
    }

    /// <remarks>
    /// The sender recovered here is kept on the transaction, so the pool does not recover it again. A signature that does
    /// not recover is left for the pool to reject.
    /// </remarks>
    private bool MatchesAnnouncedSourceAndNonce(Transaction? tx)
    {
        if (tx?.Hash is null || !_announcedSourcesAndNonces.Delete(tx.Hash.ValueHash256, out (Address Source, ulong Nonce) announced))
        {
            return true;
        }

        if (tx.Nonce != announced.Nonce)
        {
            return false;
        }

        if (tx.SenderAddress is null && ecdsa.TryRecoverAddress(tx, out Address? sender))
        {
            tx.SenderAddress = sender;
        }

        return tx.SenderAddress is null || tx.SenderAddress == announced.Source;
    }
}
