// SPDX-FileCopyrightText: 2025-2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.ExceptionServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.ScopeProvider;

internal sealed class FlatTrieWarmerScope :
    IWorldStateScopeProvider.ITrieWarmerScope,
    ITrieWarmer.IAddressWarmer
{
    private readonly ReadOnlySnapshotBundle _readOnlySnapshotBundle;
    private readonly TransientResource _transientResource;
    private readonly ITrieNodeCache _trieNodeCache;
    private readonly ITrieWarmer _trieWarmer;
    private readonly PatriciaTree _stateTree;
    private readonly ILogManager _logManager;
    private readonly Dictionary<AddressAsKey, StorageWarmer> _storageWarmers = [];
    private readonly Lock _lifetimeLock = new();
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _jobsDrained;
    private int _acceptedJobs;
    private bool _isDisposing;
    private ExceptionDispatchInfo? _disposeException;

    internal bool IsDisposing
    {
        get
        {
            lock (_lifetimeLock)
            {
                return _isDisposing;
            }
        }
    }

    public FlatTrieWarmerScope(
        in StateId baseState,
        SnapshotBundle snapshotBundle,
        ITrieNodeCache trieNodeCache,
        ITrieWarmer trieWarmer,
        ILogManager logManager)
    {
        ReadOnlySnapshotBundle? readOnlySnapshotBundle = null;
        TransientResource? transientResource = null;
        try
        {
            readOnlySnapshotBundle = snapshotBundle.TryLeaseReadOnlySnapshotBundle()
                ?? throw new ObjectDisposedException(nameof(SnapshotBundle));
            transientResource = snapshotBundle.TryLeaseTransientResource()
                ?? throw new ObjectDisposedException(nameof(SnapshotBundle));

            _readOnlySnapshotBundle = readOnlySnapshotBundle;
            _transientResource = transientResource;
            _trieNodeCache = trieNodeCache;
            _trieWarmer = trieWarmer;
            _logManager = logManager;
            _stateTree = new PatriciaTree(new StateResolver(this), logManager)
            {
                RootHash = baseState.StateRoot.ToCommitment()
            };

            _trieWarmer.OnEnterScope();
            readOnlySnapshotBundle = null;
            transientResource = null;
        }
        finally
        {
            transientResource?.ReleaseLease();
            readOnlySnapshotBundle?.Dispose();
        }
    }

    public void HintWarmAccount(in ValueAddress address)
    {
        lock (_lifetimeLock)
        {
            if (_isDisposing || !_transientResource.ShouldPrewarm(in address, null)) return;

            Address accountAddress = address.ToAddress();
            QueueAcceptedJob(() => _trieWarmer.PushAddressJob(this, accountAddress, sequenceId: 0));
        }
    }

    public void HintWarmSlot(in ValueAddress address, in UInt256 index)
    {
        lock (_lifetimeLock)
        {
            if (_isDisposing || !_transientResource.ShouldPrewarm(in address, index)) return;

            StorageWarmer? storageWarmer = GetOrCreateStorageWarmer(address.ToAddress());
            if (storageWarmer is not null)
            {
                UInt256 slot = index;
                QueueAcceptedJob(() => _trieWarmer.PushSlotJobMpmc(storageWarmer, in slot, sequenceId: 0));
            }
        }
    }

    private StorageWarmer? GetOrCreateStorageWarmer(Address address)
    {
        if (_storageWarmers.TryGetValue(address, out StorageWarmer? storageWarmer)) return storageWarmer;

        Hash256 storageRoot = _readOnlySnapshotBundle.GetAccount(address)?.StorageRoot ?? Keccak.EmptyTreeHash;
        if (storageRoot == Keccak.EmptyTreeHash) return null;

        storageWarmer = new StorageWarmer(this, address.ToAccountPath.ToHash256(), storageRoot, _logManager);
        _storageWarmers.Add(address, storageWarmer);
        return storageWarmer;
    }

    private void QueueAcceptedJob(Func<bool> queueJob)
    {
        if (_acceptedJobs++ == 0)
        {
            _jobsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        bool accepted = false;
        try
        {
            accepted = queueJob();
        }
        finally
        {
            if (!accepted) CompleteJob();
        }
    }

    public bool WarmUpStateTrie(Address address, int sequenceId)
    {
        try
        {
            _stateTree.WarmUpPath(address.ToAccountPath.Bytes);
            return true;
        }
        finally
        {
            CompleteJob();
        }
    }

    private void CompleteJob()
    {
        lock (_lifetimeLock)
        {
            if (--_acceptedJobs == 0) _jobsDrained!.SetResult();
        }
    }

    private TrieNode FindNodeOrUnknown(Hash256? address, in TreePath path, Hash256 hash)
    {
        bool found = address is null
            ? _transientResource.TryGetStateNode(in path, hash, out TrieNode? node)
            : _transientResource.TryGetStorageNode((Hash256AsKey)address, in path, hash, out node);
        if (found) return node!;

        if (_trieNodeCache.TryGet(address, in path, hash, out node)) return node;

        bool foundInSnapshot = address is null
            ? _readOnlySnapshotBundle.TryFindStateNodes(path, hash, out node)
            : _readOnlySnapshotBundle.TryFindStorageNodes(address, path, hash, out node);
        if (!foundInSnapshot)
        {
            node = new TrieNode(NodeType.Unknown, hash);
            node.MarkWarmerOwned();
        }
        return address is null
            ? _transientResource.GetOrAddStateNode(in path, node!)
            : _transientResource.GetOrAddStorageNode((Hash256AsKey)address, in path, node!);
    }

    public void Dispose()
    {
        bool ownsDisposal;
        Task? jobsDrained;
        lock (_lifetimeLock)
        {
            ownsDisposal = !_isDisposing;
            _isDisposing = true;
            jobsDrained = _acceptedJobs == 0 ? null : _jobsDrained!.Task;
        }

        if (ownsDisposal)
        {
            ExceptionDispatchInfo? disposeException = null;
            try
            {
                try
                {
                    jobsDrained?.GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    disposeException = ExceptionDispatchInfo.Capture(exception);
                }

                CaptureException(ref disposeException, _transientResource.ReleaseLease);
                CaptureException(ref disposeException, _readOnlySnapshotBundle.Dispose);
                CaptureException(ref disposeException, _trieWarmer.OnExitScope);
            }
            finally
            {
                Volatile.Write(ref _disposeException, disposeException);
                _disposeCompletion.SetResult();
            }
        }
        else
        {
            _disposeCompletion.Task.GetAwaiter().GetResult();
        }

        Volatile.Read(ref _disposeException)?.Throw();
    }

    private static void CaptureException(ref ExceptionDispatchInfo? primaryException, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            primaryException ??= ExceptionDispatchInfo.Capture(exception);
        }
    }

    private sealed class StateResolver(FlatTrieWarmerScope scope) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
        {
            TrieNode node = scope.FindNodeOrUnknown(address: null, in path, hash);
            return node.Keccak != hash
                ? throw new NodeHashMismatchException($"Node hash mismatch. Path: {path}. Hash: {node.Keccak} vs Requested: {hash}")
                : node;
        }

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            scope._readOnlySnapshotBundle.TryLoadStateRlp(in path, hash, flags);

        public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) =>
            address is null ? this : new StorageResolver(scope, address);
    }

    private sealed class StorageResolver(FlatTrieWarmerScope scope, Hash256AsKey address) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
        {
            TrieNode node = scope.FindNodeOrUnknown(address, in path, hash);
            return node.Keccak != hash
                ? throw new NodeHashMismatchException($"Node hash mismatch. Address {address.Value}. Path: {path}. Hash: {node.Keccak} vs Requested: {hash}")
                : node;
        }

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            scope._readOnlySnapshotBundle.TryLoadStorageRlp(address, in path, hash, flags);
    }

    private sealed class StorageWarmer : ITrieWarmer.IStorageWarmer
    {
        private readonly FlatTrieWarmerScope _scope;
        private readonly StorageTree _storageTree;

        public StorageWarmer(FlatTrieWarmerScope scope, Hash256 addressHash, Hash256 storageRoot, ILogManager logManager)
        {
            _scope = scope;
            _storageTree = new StorageTree(new StorageResolver(scope, addressHash), storageRoot, logManager)
            {
                RootHash = storageRoot
            };
        }

        public bool WarmUpStorageTrie(UInt256 index, int sequenceId)
        {
            try
            {
                ValueHash256 key = ValueKeccak.Zero;
                StorageTree.ComputeKeyWithLookup(index, ref key);
                _storageTree.WarmUpPath(key.BytesAsSpan);
                return true;
            }
            finally
            {
                _scope.CompleteJob();
            }
        }
    }
}
