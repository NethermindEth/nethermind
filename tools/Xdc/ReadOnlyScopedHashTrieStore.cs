// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Xdc;

/// <inheritdoc />
/// <summary>
/// Read-only <see cref="T:Nethermind.Trie.Pruning.IScopedTrieStore"/> using a Hash-only key scheme.
/// </summary>
public sealed class ReadOnlyScopedHashTrieStore(IDb db) : IScopedTrieStore
{
    public INodeStorage.KeyScheme Scheme => INodeStorage.KeyScheme.Hash;

    public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);
    public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => db.Get(hash.Bytes, flags);
    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => db.Get(hash.Bytes, flags);

    // Use the same resolver since trie nodes are stored by hash
    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => this;

    public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => throw new NotSupportedException("Read-only adapter.");

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak) => db.Get(keccak.Bytes) is not null;
}
