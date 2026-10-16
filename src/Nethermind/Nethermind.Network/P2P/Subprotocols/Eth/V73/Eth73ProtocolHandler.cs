// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V69;
using Nethermind.Network.P2P.Subprotocols.Eth.V72;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73;

/// <summary>
/// EIP-8437 <c>eth/73</c>: <c>eth/72</c> plus announcement and whole-object retrieval of EIP-8288 proof wrappers and of the
/// envelopes they list.
/// </summary>
/// <remarks>The version and the message IDs <c>0x16</c> to <c>0x1a</c> are placeholders assigned with the activating fork.</remarks>
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
            _wrapperPeer ??= wrapperTransport.Accept(this);
            // Disposal may have raced the Status; removal is idempotent.
            if (Volatile.Read(ref _disposed) != 0 && _wrapperPeer is { } accepted) wrapperTransport.Remove(accepted);
            return handled;
        }
        if (!Eth73MessageCode.IsWrapperMessage(message.PacketType)) return base.HandleMessageCore(message);
        // A wrapper message before Status is a protocol violation, rejected like any other eth message before this is reached.
        // While EIP-8288 is inactive there is no wrapper exchange; such messages establish nothing.
        if (_wrapperPeer is not { } peer)
        {
            ReportIn($"eth/73 wrapper message {message.PacketType} ignored without wrapper exchange", size);
            return true;
        }
        switch (message.PacketType)
        {
            case Eth73MessageCode.AnnounceProofWrappers:
                wrapperTransport.OnAnnounce(peer, Deserialize<AnnounceProofWrappersMessage>(message.Content));
                break;
            case Eth73MessageCode.GetProofWrappers:
                wrapperTransport.OnGetProofWrappers(peer, Deserialize<GetProofWrappersMessage>(message.Content));
                break;
            case Eth73MessageCode.ProofWrappers:
                wrapperTransport.OnProofWrappers(peer, Deserialize<ProofWrappersMessage>(message.Content));
                break;
            case Eth73MessageCode.GetWrapperEnvelopes:
                wrapperTransport.OnGetWrapperEnvelopes(peer, Deserialize<GetWrapperEnvelopesMessage>(message.Content));
                break;
            case Eth73MessageCode.WrapperEnvelopes:
                wrapperTransport.OnWrapperEnvelopes(peer, Deserialize<WrapperEnvelopesMessage>(message.Content));
                break;
        }
        ReportIn($"eth/73 wrapper message {message.PacketType}", size);
        return true;
    }

    void IWrapperLink.Send(Eth73Message message)
    {
        if (Volatile.Read(ref _disposed) != 0 || Session.IsClosing) return;
        // Serializers are resolved by the static message type, so each message is sent as its concrete type.
        switch (message)
        {
            case AnnounceProofWrappersMessage announce: Send(announce); break;
            case GetProofWrappersMessage request: Send(request); break;
            case ProofWrappersMessage response: Send(response); break;
            case GetWrapperEnvelopesMessage request: Send(request); break;
            case WrapperEnvelopesMessage response: Send(response); break;
            default: throw new ArgumentException($"{message.GetType().Name} is not a wrapper message", nameof(message));
        }
    }

    void IWrapperLink.Penalize(string reason) => Disconnect(DisconnectReason.BreachOfProtocol, reason);

    protected override void OnDisposed()
    {
        Interlocked.Exchange(ref _disposed, 1);
        if (_wrapperPeer is { } peer) wrapperTransport.Remove(peer);
        base.OnDisposed();
    }
}
