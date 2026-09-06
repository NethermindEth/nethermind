// SPDX-FileCopyrightText: 2025-2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.ScopeProvider;

internal sealed class FlatTrieWarmupSession :
    IWorldStateScopeProvider.ITrieWarmupSession,
    ITrieWarmer.IAddressWarmer
{
    private readonly ReadOnlySnapshotBundle _readOnlySnapshotBundle;
    private readonly TransientResource _transientResource;
    private readonly ITrieNodeCache _trieNodeCache;
    private readonly ITrieWarmer _trieWarmer;
    private readonly PatriciaTree _stateTree;
    private readonly ILogManager _logManager;
    private readonly ConcurrentDictionary<AddressAsKey, StorageWarmer?> _storageWarmers = [];
    private bool _isDisposed;

    public FlatTrieWarmupSession(
        in StateId baseState,
        ReadOnlySnapshotBundle readOnlySnapshotBundle,
        TransientResource transientResource,
        ITrieNodeCache trieNodeCache,
        ITrieWarmer trieWarmer,
        ILogManager logManager)
    {
        _readOnlySnapshotBundle = readOnlySnapshotBundle;
        _transientResource = transientResource;
        _trieNodeCache = trieNodeCache;
        _trieWarmer = trieWarmer;
        _logManager = logManager;
        _stateTree = new PatriciaTree(new StateResolver(this), logManager)
        {
            RootHash = baseState.StateRoot.ToCommitment()
        };
    }

    public void HintWarmAccount(in ValueAddress address)
    {
        if (Volatile.Read(ref _isDisposed) || !_transientResource.ShouldPrewarm(in address, null)) return;
        _trieWarmer.PushAddressJob(this, address.ToAddress(), sequenceId: 0);
    }

    public void HintWarmSlot(in ValueAddress address, in UInt256 index)
    {
        if (Volatile.Read(ref _isDisposed) || !_transientResource.ShouldPrewarm(in address, index)) return;

        Address accountAddress = address.ToAddress();
        StorageWarmer? storageWarmer = _storageWarmers.GetOrAdd(accountAddress, static (address, session) =>
        {
            Hash256 storageRoot = session._readOnlySnapshotBundle.GetAccount(address.Value)?.StorageRoot ?? Keccak.EmptyTreeHash;
            return storageRoot == Keccak.EmptyTreeHash
                ? null
                : new StorageWarmer(session, address.Value.ToAccountPath.ToHash256(), storageRoot, session._logManager);
        }, this);
        if (storageWarmer is not null)
        {
            _trieWarmer.PushSlotJobMpmc(storageWarmer, in index, sequenceId: 0);
        }
    }

    public bool WarmUpStateTrie(Address address, int sequenceId)
    {
        if (Volatile.Read(ref _isDisposed)) return false;
        _stateTree.WarmUpPath(address.ToAccountPath.Bytes);
        return true;
    }

    private TrieNode FindStateNodeOrUnknown(in TreePath path, Hash256 hash)
    {
        if (!_transientResource.TryGetStateNode(in path, hash, out TrieNode? node)
            && !_trieNodeCache.TryGet(address: null, in path, hash, out node))
        {
            if (!_readOnlySnapshotBundle.TryFindStateNodes(path, hash, out node))
            {
                node = CreateUnknownNode(hash);
            }

            node = _transientResource.GetOrAddStateNode(in path, node);
        }

        return ValidateNode(node, address: null, in path, hash);
    }

    private TrieNode FindStorageNodeOrUnknown(Hash256AsKey address, in TreePath path, Hash256 hash)
    {
        if (!_transientResource.TryGetStorageNode(address, in path, hash, out TrieNode? node)
            && !_trieNodeCache.TryGet(address, in path, hash, out node))
        {
            if (!_readOnlySnapshotBundle.TryFindStorageNodes(address, path, hash, out node))
            {
                node = CreateUnknownNode(hash);
            }

            node = _transientResource.GetOrAddStorageNode(address, in path, node);
        }

        return ValidateNode(node, address, in path, hash);
    }

    private static TrieNode CreateUnknownNode(Hash256 hash)
    {
        TrieNode node = new(NodeType.Unknown, hash);
        node.MarkWarmerOwned();
        return node;
    }

    private static TrieNode ValidateNode(TrieNode node, Hash256? address, in TreePath path, Hash256 hash) =>
        node.Keccak != hash
            ? throw new NodeHashMismatchException($"Node hash mismatch. Address {address}. Path: {path}. Hash: {node.Keccak} vs Requested: {hash}")
            : node;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, true)) return;

        try
        {
            _transientResource.ReleaseLease();
        }
        finally
        {
            _readOnlySnapshotBundle.Dispose();
        }
    }

    private sealed class StateResolver(FlatTrieWarmupSession session) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
            session.FindStateNodeOrUnknown(in path, hash);

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            session._readOnlySnapshotBundle.TryLoadStateRlp(in path, hash, flags);

        public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) =>
            address is null ? this : new StorageResolver(session, address);
    }

    private sealed class StorageResolver(FlatTrieWarmupSession session, Hash256AsKey address) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
            session.FindStorageNodeOrUnknown(address, in path, hash);

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            session._readOnlySnapshotBundle.TryLoadStorageRlp(address, in path, hash, flags);
    }

    private sealed class StorageWarmer(
        FlatTrieWarmupSession session,
        Hash256 addressHash,
        Hash256 storageRoot,
        ILogManager logManager) : ITrieWarmer.IStorageWarmer
    {
        private readonly StorageTree _storageTree = new(new StorageResolver(session, addressHash), storageRoot, logManager)
        {
            RootHash = storageRoot
        };

        public bool WarmUpStorageTrie(UInt256 index, int sequenceId)
        {
            if (Volatile.Read(ref session._isDisposed)) return false;

            ValueHash256 key = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(index, ref key);
            _storageTree.WarmUpPath(key.BytesAsSpan);
            return true;
        }
    }
}
