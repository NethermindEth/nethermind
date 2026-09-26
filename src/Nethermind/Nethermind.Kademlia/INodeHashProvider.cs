// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


namespace Nethermind.Kademlia;

/// <summary>
/// Maps a node/contact to its Kademlia key-space value.
/// </summary>
/// <typeparam name="TNode">The protocol-specific node/contact type.</typeparam>
/// <typeparam name="TKadKey">The key-space value used by the routing table.</typeparam>
public interface INodeHashProvider<in TNode, out TKadKey> where TKadKey : notnull
{
    /// <summary>
    /// Gets the Kademlia key-space value for <paramref name="node"/>.
    /// </summary>
    TKadKey GetHash(TNode node);
}
