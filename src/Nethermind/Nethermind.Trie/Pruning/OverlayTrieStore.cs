// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

public class OverlayTrieStore(ITrieStore baseTrieStore, IReadOnlyTrieStore store) : ITrieStore
{
    public void Dispose()
    {
        baseTrieStore.Dispose();
    }

    public bool HasRoot(Hash256 stateRoot)
    {
        return baseTrieStore.HasRoot(stateRoot);
    }

    public IScopedTrieStore GetTrieStore(Hash256? address)
    {
        return new OverlayScopedTrieStore(this, baseTrieStore.GetTrieStore(address), store.GetTrieStore(address));
    }

    public INodeStorage.KeyScheme Scheme => baseTrieStore.Scheme;

    public IBlockCommitter BeginBlockCommit(long blockNumber)
    {
        return baseTrieStore.BeginBlockCommit(blockNumber);
    }
}

public class OverlayScopedTrieStore(
    OverlayTrieStore overlayTrieStore,
    IScopedTrieStore baseScopedTrieStore,
    IScopedTrieStore globalReadOnlyState
) : IScopedTrieStore
{
    public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
    {
        TrieNode node = baseScopedTrieStore.FindCachedOrUnknown(in path, hash);
        return node.NodeType == NodeType.Unknown
            ? globalReadOnlyState.FindCachedOrUnknown(in path, hash) // no need to pass isReadOnly - IReadOnlyTrieStore overrides it as true
            : node;
    }

    public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        return baseScopedTrieStore.LoadRlp(in path, hash, flags) ?? globalReadOnlyState.LoadRlp(in path, hash, flags);
    }

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        return baseScopedTrieStore.TryLoadRlp(in path, hash, flags) ?? globalReadOnlyState.TryLoadRlp(in path, hash, flags);
    }

    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address)
    {
        return overlayTrieStore.GetTrieStore(address);
    }

    public INodeStorage.KeyScheme Scheme => baseScopedTrieStore.Scheme;

    public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None)
    {
        return baseScopedTrieStore.BeginCommit(root, writeFlags);
    }

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak)
    {
        return baseScopedTrieStore.IsPersisted(in path, in keccak);
    }
}
