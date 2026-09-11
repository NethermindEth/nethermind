// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Kademlia;

/// <summary>
/// Sends protocol-specific Kademlia requests for the core table.
/// </summary>
/// <typeparam name="TKey">The protocol-specific lookup key type.</typeparam>
/// <typeparam name="TNode">The protocol-specific node/contact type.</typeparam>
public interface IKademliaMessageSender<TKey, TNode>
{
    /// <summary>
    /// Sends a liveness probe to <paramref name="receiver"/>.
    /// </summary>
    Task<bool> Ping(TNode receiver, CancellationToken token);

    /// <summary>
    /// Requests neighbours closest to <paramref name="target"/> from <paramref name="receiver"/>.
    /// </summary>
    Task<TNode[]?> FindNeighbours(TNode receiver, TKey target, CancellationToken token);
}
