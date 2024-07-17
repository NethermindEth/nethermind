// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Kademlia;

public interface IMessageSender<THash, TValue>
{
    Task Ping(THash receiver, CancellationToken token);
    Task<THash[]> FindNeighbours(THash receiver, THash hash, CancellationToken token);
    Task<FindValueResponse<THash, TValue>> FindValue(THash receiver, THash hash, CancellationToken token);
}

public interface IMessageReceiver<THash, TValue>
{
    Task Ping(THash sender, CancellationToken token);
    Task<THash[]> FindNeighbours(THash sender, THash hash, CancellationToken token);
    Task<FindValueResponse<THash, TValue>> FindValue(THash sender, THash hash, CancellationToken token);
}

public record FindValueResponse<THash, TValue>(bool hasValue, TValue? value, THash[] neighbours);
