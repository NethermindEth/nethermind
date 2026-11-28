// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V72;
using Nethermind.Network.P2P.Subprotocols.Eth.V74;
using Nethermind.Network.P2P.Subprotocols.Eth.V76.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V76;

/// <summary>EIP-8518 <c>eth/76</c>: <c>eth/74</c> plus the exchange of EIP-8288 proof wrappers, which
/// <see cref="ProofWrapperRelay"/> holds, fetches and serves.</summary>
public class Eth76ProtocolHandler(
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
    IBlockFinder blockFinder,
    IDependencyProofStore proofStore,
    ProofWrapperRelay relay,
    ITxGossipPolicy? transactionsGossipPolicy = null)
    : Eth74ProtocolHandler(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, forkInfo,
        logManager, txPoolConfig, specProvider, blobCustodyTracker, sparseBlobPoolPeerRegistry, blockFinder, proofStore, transactionsGossipPolicy),
    IStaticProtocolInfo, IProofWrapperPeer
{
    private static readonly Func<ProofWrappersMessage, int, ProofWrappersMessage> SelectWrappers = static (message, _) => message;
    private static readonly Func<ProofWrapperManifestsMessage, int, ProofWrapperManifestsMessage> SelectManifests = static (message, _) => message;

    private MessageDictionary<GetProofWrappersMessage, ProofWrappersMessage>? _wrapperRequests;
    private MessageDictionary<GetProofWrapperManifestsMessage, ProofWrapperManifestsMessage>? _manifestRequests;
    private readonly RateLimiter _serveLimiter = new(Eth76Protocol.MaxServedRequestsPerSecond);

    private MessageDictionary<GetProofWrappersMessage, ProofWrappersMessage> WrapperRequests =>
        LazyInitializer.EnsureInitialized(ref _wrapperRequests, () => new(this));

    private MessageDictionary<GetProofWrapperManifestsMessage, ProofWrapperManifestsMessage> ManifestRequests =>
        LazyInitializer.EnsureInitialized(ref _manifestRequests, () => new(this));

    public override string Name => "eth76";

    public new static byte Version => EthVersions.Eth76;
    public override byte ProtocolVersion => Version;
    public override int MessageIdSpaceSize => Eth76Protocol.MessageIdSpaceSize;

    public override void Init()
    {
        base.Init();
        relay.AddPeer(this);
    }

    protected override void OnDisposed()
    {
        relay.RemovePeer(this);
        base.OnDisposed();
    }

    protected override bool HandleMessageCore(ZeroPacket message)
    {
        switch (message.PacketType)
        {
            case Eth76MessageCode.NewProofWrapperHashes:
                NewProofWrapperHashesMessage announcement = Deserialize<NewProofWrapperHashesMessage>(message);
                ReportIn(announcement, message.Content.Length);
                _ = relay.OnAnnouncement(this, announcement.Announcements);
                return true;
            case Eth76MessageCode.GetProofWrappers:
                HandleInBackground<GetProofWrappersMessage, ProofWrappersMessage>(message, Handle);
                return true;
            case Eth76MessageCode.ProofWrappers:
                HandleCorrelatedResponse(WrapperRequests, message, message.Content.Length, SelectWrappers);
                return true;
            case Eth76MessageCode.GetProofWrapperManifests:
                HandleInBackground<GetProofWrapperManifestsMessage, ProofWrapperManifestsMessage>(message, Handle);
                return true;
            case Eth76MessageCode.ProofWrapperManifests:
                HandleCorrelatedResponse(ManifestRequests, message, message.Content.Length, SelectManifests);
                return true;
            default:
                return base.HandleMessageCore(message);
        }
    }

    /// <remarks>A responder whose serving budget is spent answers empty, which its requester tries elsewhere without penalty.</remarks>
    private async Task<ProofWrappersMessage> Handle(GetProofWrappersMessage request, CancellationToken cancellationToken)
    {
        using GetProofWrappersMessage req = request;
        if (_serveLimiter.Backlog > Eth76Protocol.MaxServeBacklog) return new ProofWrappersMessage(req.RequestId, []);
        await _serveLimiter.WaitAsync(cancellationToken);
        return new ProofWrappersMessage(req.RequestId, relay.ServeWrappers(this, req.Hashes, req.Compact));
    }

    private async Task<ProofWrapperManifestsMessage> Handle(GetProofWrapperManifestsMessage request, CancellationToken cancellationToken)
    {
        using GetProofWrapperManifestsMessage req = request;
        if (_serveLimiter.Backlog > Eth76Protocol.MaxServeBacklog) return new ProofWrapperManifestsMessage(req.RequestId, []);
        await _serveLimiter.WaitAsync(cancellationToken);
        return new ProofWrapperManifestsMessage(req.RequestId, relay.ServeManifests(this, req.Hashes));
    }

    PublicKey IProofWrapperPeer.NodeId => Session.Node.Id;

    void IProofWrapperPeer.Announce(IReadOnlyList<WrapperAnnouncement> announcements) => Send(new NewProofWrapperHashesMessage(announcements));

    async Task<IReadOnlyList<ServedWrapper>> IProofWrapperPeer.GetProofWrappers(IReadOnlyList<ValueHash256> hashes, bool compact, CancellationToken token)
    {
        using GetProofWrappersMessage request = new([.. hashes], compact);
        Request<GetProofWrappersMessage, ProofWrappersMessage> req = new(request);
        WrapperRequests.Send(req);
        using ProofWrappersMessage response =
            await HandleResponse(req, TransferSpeedType.ProofWrappers, static _ => nameof(GetProofWrappersMessage), token);
        return response.Wrappers;
    }

    async Task<IReadOnlyList<ServedManifest>> IProofWrapperPeer.GetProofWrapperManifests(IReadOnlyList<ValueHash256> hashes, CancellationToken token)
    {
        using GetProofWrapperManifestsMessage request = new([.. hashes]);
        Request<GetProofWrapperManifestsMessage, ProofWrapperManifestsMessage> req = new(request);
        ManifestRequests.Send(req);
        using ProofWrapperManifestsMessage response =
            await HandleResponse(req, TransferSpeedType.ProofWrappers, static _ => nameof(GetProofWrapperManifestsMessage), token);
        return response.Manifests;
    }

    void IProofWrapperPeer.Disconnect(string reason) => Session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, reason);
}
