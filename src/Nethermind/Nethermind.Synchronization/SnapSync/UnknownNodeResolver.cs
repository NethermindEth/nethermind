// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.SnapSync;

/// <summary>
/// A simple ITrieNodeResolver that creates unknown nodes from hashes.
/// Used for proof resolution where RLP is already provided.
/// </summary>
internal sealed class UnknownNodeResolver : ITrieNodeResolver
{
    public static readonly UnknownNodeResolver Instance = new();

    private UnknownNodeResolver() { }

    public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
        new(NodeType.Unknown, hash);

    public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags) =>
        throw new NotSupportedException("Proof nodes have RLP embedded");

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags) => null;

    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => this;

    public INodeStorage.KeyScheme Scheme => INodeStorage.KeyScheme.Hash;
}
