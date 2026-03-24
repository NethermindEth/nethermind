// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

public interface ITrieNodeCache : IDisposable
{
    bool TryGet(Hash256? address, in TreePath path, Hash256 hash, ref TrieNodeRlp rlp);
    void Add(TransientResource transientResource);
    void Clear();
}
