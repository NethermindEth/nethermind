// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

public struct SnapshotBundleTrieProvider(SnapshotBundle bundle, bool isTrieWarmer) : ISnapshotBundleTrieProvider
{
    public TrieNode FindStateNodeOrUnknown(in TreePath path, Hash256 hash)
    {
        return bundle.FindStateNodeOrUnknown(path, hash, isTrieWarmer);
    }

    public TrieNode FindStorageNodeOrUnknown(Hash256 address, in TreePath path, Hash256 hash, int selfDestructKnownStateIdx)
    {
        return bundle.FindStorageNodeOrUnknown(address, path, hash, selfDestructKnownStateIdx, isTrieWarmer, storageInitializer: false);
    }

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags)
    {
        return bundle.TryLoadRlp(address, path, hash, flags, isTrieWarmer);
    }

    public void SetStateNode(in TreePath path, TrieNode node)
    {
        bundle.SetStateNode(path, node);
    }

    public void SetStorageNode(Hash256 address, in TreePath path, TrieNode node)
    {
        bundle.SetStorageNode(address, path, node);
    }
}
