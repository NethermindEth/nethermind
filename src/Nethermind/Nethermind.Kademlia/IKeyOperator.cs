// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Kademlia;

/// <summary>
/// Maps protocol-specific keys and nodes to the Kademlia key space.
/// </summary>
/// <typeparam name="TKey">The protocol-specific lookup key type.</typeparam>
/// <typeparam name="TNode">The protocol-specific node/contact type.</typeparam>
/// <typeparam name="TKadKey">The key-space value used by the routing table.</typeparam>
public interface IKeyOperator<TKey, in TNode, TKadKey> where TKadKey : notnull
{
    /// <summary>
    /// Gets the lookup key represented by <paramref name="node"/>.
    /// </summary>
    TKey GetKey(TNode node);

    /// <summary>
    /// Hashes a protocol-specific key into the fixed-width Kademlia key space.
    /// </summary>
    TKadKey GetKeyHash(TKey key);

    /// <summary>
    /// Hashes a protocol-specific node into the fixed-width Kademlia key space.
    /// </summary>
    TKadKey GetNodeHash(TNode node) => GetKeyHash(GetKey(node));

    /// <summary>
    /// Creates a random protocol-specific key at the requested log distance from <paramref name="nodePrefix"/>.
    /// </summary>
    TKey CreateRandomKeyAtDistance(TKadKey nodePrefix, int depth);
}
