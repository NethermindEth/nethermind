// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>An <see cref="ITrieNodeCache"/> that caches nothing.</summary>
public class NoopTrieNodeCache : ITrieNodeCache
{
    public bool TryGet(Hash256? address, in TreePath path, in ValueHash256 hash, [NotNullWhen(true)] out TrieNode? node)
    {
        node = null;
        return false;
    }

    public void Add(TransientResource transientResource) { }

    public void Clear() { }
}
