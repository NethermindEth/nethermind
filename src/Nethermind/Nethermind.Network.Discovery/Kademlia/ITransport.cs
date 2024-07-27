// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

public interface IPingMessageSender<TNode>
{
    Task Ping(TNode receiver, CancellationToken token);
}

public interface IMessageSender<TNode, TContentKey, TContent>: IPingMessageSender<TNode>
{
    Task<TNode[]> FindNeighbours(TNode receiver, ValueHash256 hash, CancellationToken token);
    Task<FindValueResponse<TNode, TContent>> FindValue(TNode receiver, TContentKey contentKey, CancellationToken token);
}

public interface IMessageReceiver<TNode, TContentKey, TContent>: IMessageSender<TNode, TContentKey, TContent>
{
}

public record FindValueResponse<TNode, TContent>(
    bool hasValue,
    TContent? value,
    TNode[] neighbours
);
