// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv4;

/// <summary>
/// Interfaces between <see cref="Kademlia{TKey,TNode}"/> and discv4. Largely handles the transport and session handling.
/// </summary>
public interface IKademliaDiscv4Adapter : IKademliaMessageSender<PublicKey, Node>, IDiscoveryMsgListener, IAsyncDisposable
{
    /// <summary>
    /// Gets or sets the message sender used to send discovery messages.
    /// </summary>
    IMsgSender? MsgSender { get; set; }

    /// <summary>
    /// Gets the session for a specific node.
    /// </summary>
    /// <param name="node">The node to get the session for.</param>
    /// <returns>The node session.</returns>
    NodeSession GetSession(Node node);

    /// <summary>
    /// Sends an ENR request to a node and returns the response.
    /// </summary>
    /// <param name="receiver">The node to send the request to.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>The ENR response message.</returns>
    Task<EnrResponseMsg> SendEnrRequest(Node receiver, CancellationToken token);
}
