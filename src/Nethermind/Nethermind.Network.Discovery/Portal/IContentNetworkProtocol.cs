// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Network.Discovery.Portal.Messages;

namespace Nethermind.Network.Discovery.Portal;

/// <summary>
/// Interface for easy(er) outgoing calls. Mainly to make RPC methods easier.
/// </summary>
public interface IContentNetworkProtocol
{
    Task<Pong> Ping(IEnr receiver, Ping ping, CancellationToken token);
    Task<Nodes> FindNodes(IEnr receiver, FindNodes findNodes, CancellationToken token);
    Task<Content> FindContent(IEnr receiver, FindContent findContent, CancellationToken token);
    Task<Accept> Offer(IEnr enr, Offer offer, CancellationToken token);
}
