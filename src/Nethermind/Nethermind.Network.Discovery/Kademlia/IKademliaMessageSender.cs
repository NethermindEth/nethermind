// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

public interface IKademliaMessageSender<TNode>
{
    Task Ping(TNode receiver, CancellationToken token);
    Task<TNode[]> FindNeighbours(TNode receiver, ValueHash256 hash, CancellationToken token);
}

public interface IKademliaMessageReceiver<TNode>: IKademliaMessageSender<TNode>
{
}

