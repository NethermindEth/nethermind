// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.ScopeProvider;

public abstract class AbstractMinimalTrieStore : IScopedTrieStore
{
    public abstract byte[]? TryLoadRlp(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None);

    /// <summary>
    /// Cache-only lookup: derived adapters can plumb in their snapshot-bundle node cache so
    /// <see cref="ITrieNodeResolver.GetOrLoadNode"/> returns the cached typed node before
    /// touching <see cref="TryLoadRlp"/>. Default returns no cached node.
    /// </summary>
    public virtual bool TryGetCachedNode(in TreePath path, in ValueHash256 hash, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TrieNode? node)
    {
        node = null;
        return false;
    }

    public virtual ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
        throw new NotSupportedException("Commit not supported");


    public byte[] LoadRlp(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None)
    {
        byte[]? value = TryLoadRlp(path, in hash, flags);
        return value ?? throw new TrieNodeException($"Missing trie node. {path}:{hash}", path, new Hash256(in hash));
    }

    public virtual ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => throw new UnsupportedOperationException("Get trie node resolver not supported");

    public INodeStorage.KeyScheme Scheme => INodeStorage.KeyScheme.HalfPath;

    public abstract class AbstractMinimalCommitter(ConcurrencyController quota) : ICommitter
    {
        public void Dispose() { }

        public abstract TrieNode CommitNode(ref TreePath path, TrieNode node);

        bool ICommitter.TryRequestConcurrentQuota() => quota.TryRequestConcurrencyQuota();
        void ICommitter.ReturnConcurrencyQuota() => quota.ReturnConcurrencyQuota();
    }

    public class UnsupportedOperationException(string message) : Exception(message);
}
