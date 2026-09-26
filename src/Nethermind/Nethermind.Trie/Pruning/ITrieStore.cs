// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    /// <summary>
    /// Provides scoped access to persisted trie nodes.
    /// </summary>
    public interface ITrieStore : IDisposable, IScopableTrieStore
    {
        bool HasRoot(Hash256 stateRoot);

        IDisposable BeginScope(BlockHeader? baseBlock);

        IScopedTrieStore GetTrieStore(Hash256? address);
    }

    public interface IScopableTrieStore
    {
        ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags);
        TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256 hash);
        byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None);
        byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None);
    }
}
