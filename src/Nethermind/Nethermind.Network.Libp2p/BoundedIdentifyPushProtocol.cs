// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;
using IdentifyMessage = Nethermind.Libp2p.Protocols.Identify.Dto.Identify;

namespace Nethermind.Network.Libp2p;

/// <summary>Receives identify pushes with the size and deadline bounds of <see cref="BoundedIdentifyProtocol"/>.</summary>
/// <remarks>Stands in for the library listener, which reads a push of any declared length with no deadline. A push updates only a peer
/// already in the peer store, which never removes an entry, so a session that was never identified or admitted cannot add one.</remarks>
public sealed class BoundedIdentifyPushProtocol(PeerStore peerStore, IdentifyProtocolSettings? settings = null) : ISessionListenerProtocol
{
    public string Id => "/ipfs/id/push/1.0.0";

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        IdentifyMessage message = await BoundedIdentifyProtocol.ReadMessageAsync(downChannel);
        if (context.State.RemotePeerId is { } remotePeerId && peerStore.TryGetPeerInfo(remotePeerId, out _))
        {
            BoundedIdentifyProtocol.Apply(message, context.State, settings ?? new IdentifyProtocolSettings(), peerStore);
        }
    }
}
