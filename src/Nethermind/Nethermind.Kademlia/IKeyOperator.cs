// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Kademlia;

/// <summary>
/// Define operations for <see cref="TKey"/> and <see cref="TNode"/>.
/// </summary>
/// <typeparam name="TKey"></typeparam>
/// <typeparam name="TNode"></typeparam>
public interface IKeyOperator<TKey, in TNode>
{
    TKey GetKey(TNode node);
    KademliaHash GetKeyHash(TKey key);
    KademliaHash GetNodeHash(TNode node) => GetKeyHash(GetKey(node));
    TKey CreateRandomKeyAtDistance(KademliaHash nodePrefix, int depth);
}
