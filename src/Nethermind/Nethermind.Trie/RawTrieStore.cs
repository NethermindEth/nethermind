// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

/// <summary>
/// Expose <see cref="ITrieStore"/> interface directly backed by <see cref="INodeStorage"/> without any pruning
/// or buffering.
/// </summary>
public class RawTrieStore(INodeStorage nodeStorage) : IReadOnlyTrieStore
{
    public RawTrieStore(IKeyValueStoreWithBatching kv) : this(new NodeStorage(kv)) { }

    void IDisposable.Dispose() { }

    public virtual ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags) =>
        new RawScopedTrieStore.Committer(nodeStorage, address, writeFlags);

    public byte[]? LoadRlp(Hash256? address, in TreePath path, in ValueHash256 hash, ReadFlags flags) =>
        nodeStorage.Get(address, path, hash, flags) ?? MissingTrieNodeException.ThrowMissing(address, in path, in hash);

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, in ValueHash256 hash, ReadFlags flags) =>
        nodeStorage.Get(address, path, hash, flags);

    public TrieNode GetOrLoadNode(Hash256? address, in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
        TrieNode.DecodeNode(in path, in hash, LoadRlp(address, in path, in hash, flags));

    public bool TryGetOrLoadNode(Hash256? address, in TreePath path, in ValueHash256 hash, [NotNullWhen(true)] out TrieNode? node, ReadFlags flags = ReadFlags.None)
    {
        byte[]? rlp = TryLoadRlp(address, in path, in hash, flags);
        if (rlp is null) { node = null; return false; }
        return TrieNode.TryDecodeNode(in path, in hash, rlp, out node);
    }

    public bool TryGetCachedNode(Hash256? address, in TreePath path, in ValueHash256 hash, [NotNullWhen(true)] out TrieNode? node)
    {
        node = null;
        return false;
    }

    public INodeStorage.KeyScheme Scheme { get; } = nodeStorage.Scheme;

    public bool HasRoot(Hash256 stateRoot) => nodeStorage.KeyExists(null, TreePath.Empty, stateRoot);

    public IDisposable BeginScope(BlockHeader? baseBlock) => new Reactive.AnonymousDisposable(static () => { });

    public IScopedTrieStore GetTrieStore(Hash256? address) => new RawScopedTrieStore(nodeStorage, address);

    public IBlockCommitter BeginBlockCommit(long blockNumber) => NullCommitter.Instance;
}
