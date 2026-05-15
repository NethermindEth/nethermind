// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

/// <summary>
/// Forwarding base for <see cref="ITrieStore"/> wrappers; defaults delegate to
/// <paramref name="inner"/>. Inheriting prevents the recurring wrapper-bypass bug where
/// the <see cref="IScopableTrieStore"/> defaults silently skip the inner cache.
/// </summary>
public abstract class WrappingTrieStore(ITrieStore inner) : ITrieStore
{
    protected ITrieStore Inner => inner;

    public virtual TrieNode GetOrLoadNode(Hash256? address, in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
        inner.GetOrLoadNode(address, in path, in hash, flags);

    public virtual bool TryGetOrLoadNode(Hash256? address, in TreePath path, in ValueHash256 hash, [NotNullWhen(true)] out TrieNode? node, ReadFlags flags = ReadFlags.None) =>
        inner.TryGetOrLoadNode(address, in path, in hash, out node, flags);

    public virtual bool TryGetCachedNode(Hash256? address, in TreePath path, in ValueHash256 hash, [NotNullWhen(true)] out TrieNode? node) =>
        inner.TryGetCachedNode(address, in path, in hash, out node);

    public virtual byte[]? LoadRlp(Hash256? address, in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
        inner.LoadRlp(address, in path, in hash, flags);

    public virtual byte[]? TryLoadRlp(Hash256? address, in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
        inner.TryLoadRlp(address, in path, in hash, flags);

    public virtual INodeStorage.KeyScheme Scheme => inner.Scheme;

    public virtual ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags) =>
        inner.BeginCommit(address, root, writeFlags);

    public virtual IBlockCommitter BeginBlockCommit(long blockNumber) => inner.BeginBlockCommit(blockNumber);

    public virtual bool HasRoot(Hash256 stateRoot) => inner.HasRoot(stateRoot);

    public virtual bool HasRoot(Hash256 stateRoot, long blockNumber) => inner.HasRoot(stateRoot, blockNumber);

    public virtual IDisposable BeginScope(BlockHeader? baseBlock) => inner.BeginScope(baseBlock);

    public virtual IScopedTrieStore GetTrieStore(Hash256? address) => new ScopedTrieStore(this, address);

    public virtual void Dispose() => inner.Dispose();
}
