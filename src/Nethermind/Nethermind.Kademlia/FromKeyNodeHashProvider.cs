// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


namespace Nethermind.Kademlia;

public class FromKeyNodeHashProvider<TKey, TNode>(IKeyOperator<TKey, TNode> keyOperator) : INodeHashProvider<TNode>
{
    public KademliaHash GetHash(TNode node) => keyOperator.GetNodeHash(node);
}
