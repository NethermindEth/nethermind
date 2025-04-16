// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

/// <summary>
/// Should be exposed by application to kademlia so that kademlia can send out message.
/// </summary>
/// <typeparam name="TNode"></typeparam>
public interface IKademliaMessageSender<TNode>
{
    Task Ping(TNode receiver, CancellationToken token);
    Task<TNode[]> FindNeighbours(TNode receiver, ValueHash256 hash, CancellationToken token);
}

/// <summary>
/// Application should call this class on incoming messages.
/// </summary>
/// <typeparam name="TNode"></typeparam>
public interface IKademliaMessageReceiver<TNode>: IKademliaMessageSender<TNode>
{
}

