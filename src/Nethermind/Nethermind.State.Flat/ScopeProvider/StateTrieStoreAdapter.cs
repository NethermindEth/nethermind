// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.ScopeProvider;

internal interface ISnapshotBundleTrieProvider
{
    TrieNode FindStateNodeOrUnknown(in TreePath path, Hash256 hash, bool isTrieWarmer);
    TrieNode FindStorageNodeOrUnknown(Hash256 address, in TreePath path, Hash256 hash, int selfDestructKnownStateIdx, bool isTrieWarmer);
    byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags, bool isTrieWarmer);

    void SetStateNode(in TreePath path, TrieNode node);
    void SetStorageNode(Hash256 address, in TreePath path, TrieNode node);
}

internal class StateTrieStoreAdapter<TTrieProvider>(
    TTrieProvider bundle,
    ConcurrencyQuota concurrencyQuota,
    bool isTrieWarmer
) : AbstractMinimalTrieStore
    where TTrieProvider : struct, ISnapshotBundleTrieProvider
{
    private TTrieProvider _bundle = bundle;

    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
    {
        return _bundle.FindStateNodeOrUnknown(path, hash, isTrieWarmer);
    }

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => _bundle.TryLoadRlp(null, path, hash, flags, isTrieWarmer);

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => new Committer(_bundle, concurrencyQuota);

    public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address)
    {
        if (address is null) return this;
        // Used in trie visitor and weird very edge case that cuts the whole thing to peaces
        return new StorageTrieStoreAdapter<TTrieProvider>(_bundle, concurrencyQuota, address, -1, isTrieWarmer);
    }

    private class Committer(ISnapshotBundleTrieProvider bundle, ConcurrencyQuota concurrencyQuota) : AbstractMinimalCommitter(concurrencyQuota)
    {
        public override TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            bundle.SetStateNode(path, node);
            return node;
        }
    }
}

internal class StorageTrieStoreAdapter<TTrieProvider> (
    TTrieProvider bundle,
    ConcurrencyQuota concurrencyQuota,
    Hash256AsKey addressHash,
    int selfDestructKnownStateIdx,
    bool isTrieWarmer
): AbstractMinimalTrieStore
    where TTrieProvider : struct, ISnapshotBundleTrieProvider
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

    private class Committer(ISnapshotBundleTrieProvider bundle, Hash256AsKey addressHash, ConcurrencyQuota concurrencyQuota) : AbstractMinimalCommitter(concurrencyQuota)
    {
        public override TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            bundle.SetStorageNode(addressHash, path, node);
            return node;
        }
    }
}
