// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

/// <summary>
/// Translate the node and/or the content key into a ValueHash256 which is finally used for implementing
/// the distance calculation.
/// Should this get replaced with an INode.GetHash where TNode need to implement INode? I can't decide.
/// </summary>
/// <typeparam name="TNode"></typeparam>
/// <typeparam name="TContentKey"></typeparam>
public interface INodeHashProvider<in TNode, in TContentKey>
{
    ValueHash256 GetHash(TNode node);
    ValueHash256 GetHash(TContentKey key);
}
