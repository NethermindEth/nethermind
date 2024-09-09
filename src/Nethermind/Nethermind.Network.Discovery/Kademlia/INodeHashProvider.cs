// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

/// <summary>
/// Translate the node and/or the content key into a ValueHash256 which is finally used for implementing
/// the distance calculation.
/// Should this get replaced with an INode.GetHash where TNode need to implement INode? I can't decide. That would make
/// the internal methods cleaner, but it would mean TNode need to be a wrapper or have to implement some interface,
/// which may not be possible. One of the important optimization is to have a cached TNode[], so if TNode is a wrapper,
/// it would need to be unwrapped during serialization. But then again, it could be insignificant or the serialization
/// could be specialized.
/// </summary>
/// <typeparam name="TNode"></typeparam>
/// <typeparam name="TContentKey"></typeparam>
public interface INodeHashProvider<in TNode, in TContentKey>
{
    ValueHash256 GetHash(TNode node);
    ValueHash256 GetHash(TContentKey key);
}
