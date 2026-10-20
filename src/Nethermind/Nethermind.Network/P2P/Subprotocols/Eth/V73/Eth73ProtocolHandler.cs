// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Eth.V69;
using Nethermind.Network.P2P.Subprotocols.Eth.V72;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using GetPooledTransactionsMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73;

/// <summary>
/// EIP-8437 <c>eth/73</c>: <c>eth/72</c> plus announcement and retrieval of EIP-8288 proof wrappers, with the envelopes they
/// list recovered and served through GetPooledTransactions.
/// </summary>
/// <remarks>The protocol version and the message IDs <c>0x16</c> to <c>0x18</c> are provisional.</remarks>
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
    ProofWrapperTransport wrapperTransport,
    ITxGossipPolicy? transactionsGossipPolicy = null)
    : Eth72ProtocolHandler(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, forkInfo,
        logManager, txPoolConfig, specProvider, blobCustodyTracker, sparseBlobPoolPeerRegistry, transactionsGossipPolicy), IStaticProtocolInfo,
    IWrapperLink
{
    private WrapperPeer? _wrapperPeer;
    private int _disposed;

    public override string Name => "eth73";

    /// <summary>Gets the eth/73 protocol version.</summary>
    public new static byte Version => EthVersions.Eth73;
    public override byte ProtocolVersion => Version;
    public override int MessageIdSpaceSize => Eth73Protocol.MessageIdSpaceSize;

    string IWrapperLink.Description => Session.ToString() ?? "eth/73 peer";

    protected override bool HandleMessageCore(ZeroPacket message)
    {
        int size = message.Content.ReadableBytes;
        if (message.PacketType == Eth69MessageCode.Status)
        {
            // Status is unchanged from eth/72; peers negotiate no verifier.
            bool handled = base.HandleMessageCore(message);
            if (Volatile.Read(ref _wrapperPeer) is null && wrapperTransport.Accept(this) is { } accepted)
                // The interlocked publish fences against OnDisposed: either it sees the peer or this sees the disposal.
                Interlocked.CompareExchange(ref _wrapperPeer, accepted, null);
            if (Volatile.Read(ref _disposed) != 0 && Volatile.Read(ref _wrapperPeer) is { } registered) wrapperTransport.Remove(registered);
            return handled;
        }
        if (message.PacketType == Eth66MessageCode.PooledTransactions && TryHandleEnvelopes(message, size)) return true;
        if (!Eth73MessageCode.IsWrapperMessage(message.PacketType)) return base.HandleMessageCore(message);
        // A wrapper message before Status is a protocol violation, rejected like any other eth message before this is reached.
        // While EIP-8288 is inactive there is no wrapper exchange; such messages establish nothing.
        if (Volatile.Read(ref _wrapperPeer) is not { } peer)
        {
            ReportIn($"eth/73 wrapper message {message.PacketType} ignored without wrapper exchange", size);
            return true;
        }
        switch (message.PacketType)
        {
            case Eth73MessageCode.NewProofWrapperHashes:
                wrapperTransport.OnAnnounce(peer, Deserialize<NewProofWrapperHashesMessage>(message.Content));
                break;
            case Eth73MessageCode.GetProofWrappers:
                wrapperTransport.OnGetProofWrappers(peer, Deserialize<GetProofWrappersMessage>(message.Content));
                break;
            case Eth73MessageCode.ProofWrappers:
                wrapperTransport.OnProofWrappers(peer, Deserialize<ProofWrappersMessage>(message.Content));
                break;
        }
        ReportIn($"eth/73 wrapper message {message.PacketType}", size);
        return true;
    }

    /// <summary>Hands a PooledTransactions response to an envelope-recovery request to the wrapper transport.</summary>
    /// <returns>False for any other response, which the <c>eth/72</c> handling correlates with its own requests.</returns>
    private bool TryHandleEnvelopes(ZeroPacket message, int size)
    {
        if (Volatile.Read(ref _wrapperPeer) is not { } peer || !Eth66RequestId.TryPeek(message.Content, out long requestId)
            || !wrapperTransport.IsEnvelopeRequest(peer, requestId)) return false;
        byte[] data = new byte[message.Content.ReadableBytes];
        message.Content.ReadBytes(data);
        (long id, byte[][] envelopes) = PooledEnvelopes.Decode(data);
        wrapperTransport.OnPooledEnvelopes(peer, id, envelopes);
        ReportIn($"eth/73 PooledTransactions with {envelopes.Length} recovered envelopes", size);
        return true;
    }

    /// <summary>On <c>eth/73</c> a dependency-bearing envelope is served like any pooled transaction; below it, it is
    /// unavailable.</summary>
    protected override bool ServesDependencyBearingTransactions => true;

    /// <summary>On <c>eth/73</c> pooled dependency-bearing transactions propagate through Transactions and
    /// NewPooledTransactionHashes alongside their covering wrappers' announcements, under the existing fan-out.</summary>
    protected override bool PropagatesDependencyBearingTransactions => true;

    /// <summary>Retains a pushed dependency-bearing envelope for a covering wrapper; it is not admitted or relayed before
    /// that wrapper is fully validated.</summary>
    protected override void HandleDependencyBearingTransaction(Transaction tx)
    {
        if (Volatile.Read(ref _wrapperPeer) is { } peer) wrapperTransport.RetainEnvelope(peer, tx);
    }

    /// <summary>Serves from the pool, then from the wrapper-envelope cache, which keeps every envelope a held wrapper lists
    /// after pool rejection or eviction.</summary>
    protected override bool TryGetPooledTransactionToServe(in ValueHash256 hash, [NotNullWhen(true)] out Transaction? tx)
        => base.TryGetPooledTransactionToServe(hash, out tx) || wrapperTransport.TryGetEnvelope(hash, out tx);

    void IWrapperLink.Send(P2PMessage message)
    {
        if (Volatile.Read(ref _disposed) != 0 || Session.IsClosing)
        {
            message.Dispose();
            return;
        }
        // Serializers are resolved by the static message type, so each message is sent as its concrete type.
        switch (message)
        {
            case NewProofWrapperHashesMessage announce: Send(announce); break;
            case GetProofWrappersMessage request: Send(request); break;
            case ProofWrappersMessage response: Send(response); break;
            case GetPooledTransactionsMessage66 request: Send(request); break;
            default: throw new ArgumentException($"{message.GetType().Name} is not a wrapper message", nameof(message));
        }
    }

    void IWrapperLink.Penalize(string reason) => Disconnect(DisconnectReason.BreachOfProtocol, reason);

    /// <summary>Counts an envelope pushed without a covering wrapper as an unaccepted transaction, lowering the peer's gossip
    /// standing without disconnecting it.</summary>
    void IWrapperLink.LowerScore(string reason)
    {
        if (Logger.IsDebug) Logger.Debug($"{Session} {reason}");
        ReportUnacceptedTransaction(AcceptTxResult.MissingDependencyProof);
    }

    /// <summary>Checks a recovered envelope against the type and size this peer announced for it, taking the announcement as
    /// a delivered PooledTransactions entry does.</summary>
    bool IWrapperLink.MatchesAnnouncedShape(Transaction transaction) =>
        transaction.Hash is not { } hash || !TryTakeTxShape(hash.ValueHash256, out (int Size, TxType Type) shape)
        || transaction.Type == shape.Type && MatchesAnnouncedTransactionSize(transaction, shape.Size);

    protected override void OnDisposed()
    {
        Interlocked.Exchange(ref _disposed, 1);
        if (Volatile.Read(ref _wrapperPeer) is { } peer) wrapperTransport.Remove(peer);
        base.OnDisposed();
    }
}
