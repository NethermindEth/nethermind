// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

public class FromKeyNodeHashProvider<TKey, TNode>(IKeyOperator<TKey, TNode> keyOperator) : INodeHashProvider<TNode>
{
    public ValueHash256 GetHash(TNode node) => keyOperator.GetNodeHash(node);
}
