// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

public interface ITrieNodeCache
{
    bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node);
    /// <summary>Returns the matching cached node, or publishes and returns the supplied node.</summary>
    TrieNode GetOrAdd(Hash256? address, in TreePath path, TrieNode node);
    /// <summary>Prunes transient child references and performs shared-cache memory maintenance without promoting transient nodes.</summary>
    void Add(TransientResource transientResource);
    void Clear();
}
