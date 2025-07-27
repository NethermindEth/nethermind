// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

/// <summary>
/// Should be exposed by application to kademlia so that kademlia can send out message.
/// </summary>
/// <typeparam name="TKey"></typeparam>
/// <typeparam name="TNode"></typeparam>
public interface IKademliaMessageSender<TKey, TNode>
{
    Task Ping(TNode receiver, CancellationToken token);
    Task<TNode[]> FindNeighbours(TNode receiver, TKey target, CancellationToken token);
}

