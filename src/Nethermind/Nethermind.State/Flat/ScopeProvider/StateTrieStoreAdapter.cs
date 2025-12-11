// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.ScopeProvider;

internal class StateTrieStoreAdapter(
    SnapshotBundle bundle,
    ConcurrencyQuota concurrencyQuota,
    bool isTrieWarmer
) : AbstractMinimalTrieStore
{
    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
    {
        return bundle.FindStateNodeOrUnknown(path, hash, isTrieWarmer);
    }

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => bundle.TryLoadRlp(null, path, hash, flags, isTrieWarmer);

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => new Committer(bundle, concurrencyQuota);

    public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address)
    {
        // Used in trie visitor and weird very edge case that cuts the whole thing to peaces
        return new StorageTrieStoreAdapter(bundle, concurrencyQuota, address, -1, isTrieWarmer);
    }

    private class Committer(SnapshotBundle bundle, ConcurrencyQuota concurrencyQuota) : AbstractMinimalCommitter(concurrencyQuota)
    {
        public override TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            bundle.SetStateNode(path, node);
            return node;
        }
    }
}

internal class StorageTrieStoreAdapter(
    SnapshotBundle bundle,
    ConcurrencyQuota concurrencyQuota,
    Hash256AsKey addressHash,
    int selfDestructKnownStateIdx,
    bool isTrieWarmer
): AbstractMinimalTrieStore
{
    internal int SelfDestructKnownStateIdx = selfDestructKnownStateIdx;

    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
    {
        return bundle.FindStorageNodeOrUnknown(addressHash, path, hash, SelfDestructKnownStateIdx, isTrieWarmer);
    }

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        return bundle.TryLoadRlp(addressHash, in path, hash, flags, isTrieWarmer);
    }

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None)
    {
        return new Committer(bundle, addressHash, concurrencyQuota);
    }

    private class Committer(SnapshotBundle bundle, Hash256AsKey addressHash, ConcurrencyQuota concurrencyQuota) : AbstractMinimalCommitter(concurrencyQuota)
    {
        public override TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            bundle.SetStorageNodeBatched(addressHash, path, node);
            return node;
        }
    }
}
