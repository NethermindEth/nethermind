// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// Adapts FlatDb's trie node data into <see cref="ITrieStore"/> for trie-based operations
/// such as witness generation. Lazily creates <see cref="ReadOnlySnapshotBundle"/> and
/// <see cref="ReadOnlyStateTrieStoreAdapter"/> on <see cref="BeginScope"/>.
/// All commit operations are no-ops (read-only usage).
/// </summary>
internal sealed class FlatReadOnlyTrieStore(IFlatDbManager flatDbManager) : IReadOnlyTrieStore
{
    private ReadOnlySnapshotBundle? _bundle;
    private ReadOnlyStateTrieStoreAdapter? _adapter;

    // IScopableTrieStore — delegate to adapter (set after BeginScope)
    public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256 hash) =>
        Resolve(address).FindCachedOrUnknown(in path, hash);

    public byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        Resolve(address).LoadRlp(in path, hash, flags);

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        Resolve(address).TryLoadRlp(in path, hash, flags);

    public INodeStorage.KeyScheme Scheme =>
        _adapter?.Scheme ?? INodeStorage.KeyScheme.HalfPath;

    // ITrieStore
    public bool HasRoot(Hash256 stateRoot) => true;

    public bool HasRoot(Hash256 stateRoot, long blockNumber) =>
        flatDbManager.HasStateForBlock(new StateId(blockNumber, stateRoot));

    public IDisposable BeginScope(BlockHeader? baseBlock)
    {
        _bundle = flatDbManager.GatherReadOnlySnapshotBundle(new StateId(baseBlock))
            ?? throw new InvalidOperationException($"State at {baseBlock} not found");
        _adapter = new ReadOnlyStateTrieStoreAdapter(_bundle);
        return new ScopeCleanup(this);
    }

    public IScopedTrieStore GetTrieStore(Hash256? address) => new ScopedTrieStore(this, address);

    public IBlockCommitter BeginBlockCommit(long blockNumber) => NullCommitter.Instance;

    public ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags) => NullCommitter.Instance;

    public void Dispose()
    {
        _bundle?.Dispose();
        _bundle = null;
        _adapter = null;
    }

    private ITrieNodeResolver Resolve(Hash256? address)
    {
        ReadOnlyStateTrieStoreAdapter adapter = _adapter ?? throw new InvalidOperationException("BeginScope has not been called");
        return address is null ? adapter : adapter.GetStorageTrieNodeResolver(address);
    }

    private sealed class ScopeCleanup(FlatReadOnlyTrieStore store) : IDisposable
    {
        public void Dispose()
        {
            store._bundle?.Dispose();
            store._bundle = null;
            store._adapter = null;
        }
    }
}
