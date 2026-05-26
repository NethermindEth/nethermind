// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Sparse;

/// <summary>
/// <see cref="ITrieNodeReader"/> backed by <see cref="INodeStorage"/> (HalfPath or Hash key scheme).
/// Uses both path and hash for key construction.
/// </summary>
public sealed class HalfPathTrieNodeReader(INodeStorage nodeStorage) : ITrieNodeReader
{
    public byte[] LoadStateRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        ValueHash256 valueHash = hash.ValueHash256;
        byte[] rlp = nodeStorage.Get(null, path, in valueHash, flags)
            ?? throw new MissingTrieNodeException("State trie node not found", null, path, hash);
        return rlp;
    }

    public byte[] LoadStorageRlp(Hash256 accountPathHash, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        ValueHash256 valueHash = hash.ValueHash256;
        byte[] rlp = nodeStorage.Get(accountPathHash, path, in valueHash, flags)
            ?? throw new MissingTrieNodeException("Storage trie node not found", accountPathHash, path, hash);
        return rlp;
    }
}
