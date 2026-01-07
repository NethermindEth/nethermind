// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

public struct ReadOnlySnapshotBundleTrieProvider(ReadOnlySnapshotBundle snapshotBundle) : ISnapshotBundleTrieProvider
{
    public TrieNode FindStateNodeOrUnknown(in TreePath path, Hash256 hash)
    {
        return snapshotBundle.TryFindStateNodes(path, hash, out var node) ? node : new TrieNode(NodeType.Unknown, hash);
    }

    public TrieNode FindStorageNodeOrUnknown(Hash256 address, in TreePath path, Hash256 hash, int selfDestructKnownStateIdx)
    {
        return snapshotBundle.TryFindStorageNodes(address, path, hash, selfDestructKnownStateIdx, out TrieNode? node) ? node :  new TrieNode(NodeType.Unknown, hash);
    }

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags)
    {
        return snapshotBundle.TryLoadRlp(address, in path, hash, flags);
    }

    public void SetStateNode(in TreePath path, TrieNode node)
    {
        throw new InvalidOperationException("Cannot set new node in a readonly snapshot bundle");
    }

    public void SetStorageNode(Hash256 address, in TreePath path, TrieNode node)
    {
        throw new InvalidOperationException("Cannot set new node in a readonly snapshot bundle");
    }
}
