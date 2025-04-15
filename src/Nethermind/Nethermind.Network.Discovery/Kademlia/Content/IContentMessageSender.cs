// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Kademlia.Content;

public interface IContentMessageSender<TNode, TContentKey, TContent>
{
    Task<FindValueResponse<TNode, TContent>> FindValue(TNode receiver, TContentKey contentKey, CancellationToken token);
}

public interface IContentMessageReceiver<TNode, TContentKey, TContent>: IContentMessageSender<TNode, TContentKey, TContent>
{
}

public record FindValueResponse<TNode, TContent>(
    bool HasValue,
    TContent? Value,
    TNode[] Neighbours
);
