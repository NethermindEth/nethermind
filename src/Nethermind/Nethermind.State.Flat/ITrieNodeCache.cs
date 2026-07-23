// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

public interface ITrieNodeCache
{
    bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node);

    /// <summary>Variant of <see cref="TryGet(Hash256?, in TreePath, Hash256, out TrieNode?)"/> for hash-per-request callers.</summary>
    /// <remarks>The default is a compatibility fallback that allocates a <see cref="Hash256"/>;
    /// allocation-sensitive implementations should override it.</remarks>
    bool TryGet(Hash256? address, in TreePath path, in ValueHash256 hash, [NotNullWhen(true)] out TrieNode? node) =>
        TryGet(address, in path, hash.ToCommitment(), out node);

    void Add(TransientResource transientResource);
    void Clear();
}
