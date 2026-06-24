// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


namespace Nethermind.Kademlia;

public class FromKeyNodeHashProvider<TKey, TNode, TKadKey>(IKeyOperator<TKey, TNode, TKadKey> keyOperator) : INodeHashProvider<TNode, TKadKey>
    where TKadKey : notnull
{
    public TKadKey GetHash(TNode node) => keyOperator.GetNodeHash(node);
}
