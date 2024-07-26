// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

public interface IMessageSender<TNode, TValue>
{
    Task Ping(TNode receiver, CancellationToken token);
    Task<TNode[]> FindNeighbours(TNode receiver, ValueHash256 hash, CancellationToken token);
    Task<FindValueResponse<TNode, TValue>> FindValue(TNode receiver, ValueHash256 hash, CancellationToken token);
}

public interface IMessageReceiver<TNode, TValue>
{
    Task Ping(TNode sender, CancellationToken token);
    Task<TNode[]> FindNeighbours(TNode sender, ValueHash256 hash, CancellationToken token);
    Task<FindValueResponse<TNode, TValue>> FindValue(TNode sender, ValueHash256 hash, CancellationToken token);
}

public record FindValueResponse<TNode, TValue>(bool hasValue, TValue? value, TNode[] neighbours);
