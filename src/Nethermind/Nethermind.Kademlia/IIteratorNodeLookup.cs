// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Kademlia;

public interface IIteratorNodeLookup<TKey, TNode>
{
    IAsyncEnumerable<TNode> Lookup(TKey target, CancellationToken token);
}
