// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.ScopeProvider;

public abstract class AbstractMinimalTrieStore : IScopedTrieStore
{
    public abstract TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash);

    public abstract byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None);
    public abstract ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None);


    public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        byte[]? value = TryLoadRlp(path, hash, flags);
        if (value is null)
        {
            throw new TrieNodeException($"Missing trie node. {path}:{hash}", path, hash);
        }

        return value;
    }

    public virtual ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => throw new UnsupportedOperationException("Get trie node resolver not supported");

    public INodeStorage.KeyScheme Scheme => INodeStorage.KeyScheme.HalfPath;

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak) => throw new UnsupportedOperationException("Persisted check not supported");

    public abstract class AbstractMinimalCommitter(ConcurrencyController quota) : ICommitter
    {
        public void Dispose() { }

        public abstract TrieNode CommitNode(ref TreePath path, TrieNode node);

        bool ICommitter.TryRequestConcurrentQuota() => quota.TryRequestConcurrencyQuota();
        void ICommitter.ReturnConcurrencyQuota() => quota.ReturnConcurrencyQuota();
    }

    public class UnsupportedOperationException(string message) : Exception(message);
}
