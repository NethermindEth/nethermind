// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

public interface IMessageSender<TNode>
{
    Task Ping(TNode receiver, CancellationToken token);
    Task<TNode[]> FindNeighbours(TNode receiver, ValueHash256 hash, CancellationToken token);
    Task<FindValueResponse<TNode>> FindValue(TNode receiver, ValueHash256 hash, CancellationToken token);
}

public interface IMessageReceiver<TNode>
{
    Task Ping(TNode sender, CancellationToken token);
    Task<TNode[]> FindNeighbours(TNode sender, ValueHash256 hash, CancellationToken token);
    Task<FindValueResponse<TNode>> FindValue(TNode sender, ValueHash256 hash, CancellationToken token);
}

public record FindValueResponse<TNode>(
    bool hasValue,
    byte[]? value, // I tried making value generic also....
    TNode[] neighbours
);
