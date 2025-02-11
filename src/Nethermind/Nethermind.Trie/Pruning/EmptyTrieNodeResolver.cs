// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

/// <summary>
/// For use case where `ITrieNodeResolver` should not get called.
/// </summary>
public class EmptyTrieNodeResolver : ITrieNodeResolver
{
    public static EmptyTrieNodeResolver Instance = new EmptyTrieNodeResolver();

    public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
    {
        throw new InvalidOperationException("Empty node resolver should not be called");
    }

    public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        throw new InvalidOperationException("Empty node resolver should not be called");
    }

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        throw new InvalidOperationException("Empty node resolver should not be called");
    }

    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address)
    {
        throw new InvalidOperationException("Empty node resolver should not be called");
    }

    public INodeStorage.KeyScheme Scheme => INodeStorage.KeyScheme.Hash;
}
