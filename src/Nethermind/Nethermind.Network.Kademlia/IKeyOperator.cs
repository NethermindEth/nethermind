// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Kademlia;

/// <summary>
/// Define operations for <see cref="THash"/> and <see cref="TNode"/>.
/// </summary>
/// <typeparam name="THash"></typeparam>
/// <typeparam name="TNode"></typeparam>
public interface IKeyOperator<TPublicKey, THash, in TNode> where THash : struct
{
    TPublicKey GetKey(TNode node);
    THash GetKeyHash(TPublicKey key);
    THash GetNodeHash(TNode node) => GetKeyHash(GetKey(node));
    TPublicKey CreateRandomKeyAtDistance(THash nodePrefix, int depth);
}
