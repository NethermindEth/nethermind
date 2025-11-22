// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie.Flat;

public class FlatTrieStore: ITrieStore
{
    public void Dispose()
    {
        throw new NotImplementedException();
    }

    public ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags)
    {
        throw new NotImplementedException();
    }

    public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256 hash)
    {
        throw new NotImplementedException();
    }

    public byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        throw new NotImplementedException();
    }

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        throw new NotImplementedException();
    }

    public bool IsPersisted(Hash256? address, in TreePath path, in ValueHash256 keccak)
    {
        throw new NotImplementedException();
    }

    public INodeStorage.KeyScheme Scheme => INodeStorage.KeyScheme.HalfPath;
    public bool HasRoot(Hash256 stateRoot)
    {
        throw new NotImplementedException();
    }

    public IDisposable BeginScope(BlockHeader? baseBlock)
    {
        throw new NotImplementedException();
    }

    public IScopedTrieStore GetTrieStore(Hash256? address)
    {
        throw new NotImplementedException();
    }

    public IBlockCommitter BeginBlockCommit(long blockNumber)
    {
        throw new NotImplementedException();
    }
}
