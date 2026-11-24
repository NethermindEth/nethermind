// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V72;
using Nethermind.Network.P2P.Subprotocols.Eth.V75;
using Nethermind.Network.P2P.Subprotocols.Eth.V75.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V76.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V76;

/// <summary>
/// <c>eth/76</c>: <c>eth/75</c> plus relay of mode-1 (recursive proof) wrappers, coverage hints in announcements, compact
/// retrieval and transaction manifests.
/// </summary>
/// <remarks>The protocol version and the message IDs <c>0x1b</c> and <c>0x1c</c> are provisional. Message shapes follow the
/// negotiated version, not the wrapper mode.</remarks>
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
    ProofWrapperBroadcaster wrapperBroadcaster,
    ITxGossipPolicy? transactionsGossipPolicy = null)
    : Eth75ProtocolHandler(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, forkInfo,
        logManager, txPoolConfig, specProvider, blobCustodyTracker, sparseBlobPoolPeerRegistry, blockFinder, proofStore, wrapperBroadcaster,
        transactionsGossipPolicy),
    IStaticProtocolInfo
{
    public override string Name => "eth76";

    /// <summary>Gets the eth/76 protocol version.</summary>
    public new static byte Version => EthVersions.Eth76;
    public override byte ProtocolVersion => Version;
    public override int MessageIdSpaceSize => Eth76Protocol.MessageIdSpaceSize;

    private protected override bool IsWrapperMessage(int code) => Eth76MessageCode.IsWrapperMessage(code);

    private protected override void HandleWrapperMessage(WrapperPeer peer, ZeroPacket message)
    {
        switch (message.PacketType)
        {
            case Eth75MessageCode.NewProofWrapperHashes:
                WrapperBroadcaster.OnAnnounce(peer, Deserialize<NewProofWrapperHashesMessage76>(message.Content));
                break;
            case Eth75MessageCode.GetProofWrappers:
                WrapperBroadcaster.OnGetProofWrappers(peer, Deserialize<GetProofWrappersMessage76>(message.Content));
                break;
            case Eth76MessageCode.GetProofWrapperManifests:
                WrapperBroadcaster.OnGetProofWrapperManifests(peer, Deserialize<GetProofWrapperManifestsMessage>(message.Content));
                break;
            case Eth76MessageCode.ProofWrapperManifests:
                WrapperBroadcaster.OnProofWrapperManifests(peer, Deserialize<ProofWrapperManifestsMessage>(message.Content));
                break;
            default:
                base.HandleWrapperMessage(peer, message);
                break;
        }
    }

    /// <remarks>The broadcaster builds version-neutral announcements and requests; they take their <c>eth/76</c> form here.</remarks>
    private protected override void SendWrapperMessage(P2PMessage message)
    {
        switch (message)
        {
            case NewProofWrapperHashesMessage76 announce: Send(announce); break;
            case NewProofWrapperHashesMessage announce: Send(new NewProofWrapperHashesMessage76(announce.Announcements)); break;
            case GetProofWrappersMessage76 request: Send(request); break;
            case GetProofWrappersMessage request: Send(new GetProofWrappersMessage76(request.RequestId, request.Compact, request.Hashes)); break;
            case ProofWrapperManifestsMessage manifests: Send(manifests); break;
            default: base.SendWrapperMessage(message); break;
        }
    }
}
