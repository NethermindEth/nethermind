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

internal sealed class FlatTrieWarmupSession :
    IWorldStateScopeProvider.ITrieWarmupSession,
    ITrieWarmer.IAddressWarmer
{
    private readonly SnapshotBundle _snapshotBundle;
    private readonly ReadOnlySnapshotBundle _readOnlySnapshotBundle;
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

    internal Action? OnWaitingForJobs;

    public FlatTrieWarmupSession(
        in StateId baseState,
        SnapshotBundle snapshotBundle,
        ITrieWarmer trieWarmer,
        ILogManager logManager)
    {
        ReadOnlySnapshotBundle? readOnlySnapshotBundle = null;
        try
        {
            readOnlySnapshotBundle = snapshotBundle.TryLeaseReadOnlySnapshotBundle()
                ?? throw new ObjectDisposedException(nameof(SnapshotBundle));

            _snapshotBundle = snapshotBundle;
            _readOnlySnapshotBundle = readOnlySnapshotBundle;
            _trieWarmer = trieWarmer;
            _logManager = logManager;
            _stateTree = new PatriciaTree(new StateResolver(this), logManager)
            {
                RootHash = baseState.StateRoot.ToCommitment()
            };

            _trieWarmer.OnEnterScope();
            readOnlySnapshotBundle = null;
        }
        finally
        {
            readOnlySnapshotBundle?.Dispose();
        }
    }

    public void HintWarmAccount(in ValueAddress address)
    {
        if (!_snapshotBundle.ShouldQueuePrewarm(in address)) return;

        lock (_lifetimeLock)
        {
            if (_isDisposing) return;

            AcceptJob();
            bool accepted = false;
            try
            {
                accepted = _trieWarmer.PushAddressJob(this, address.ToAddress(), sequenceId: 0);
            }
            finally
            {
                if (!accepted) CompleteJobUnderLock();
            }
        }
    }

    public void HintWarmSlot(in ValueAddress address, in UInt256 index)
    {
        if (!_snapshotBundle.ShouldQueuePrewarm(in address, index)) return;

        Address accountAddress = address.ToAddress();
        StorageWarmer? storageWarmer;
        lock (_lifetimeLock)
        {
            if (_isDisposing) return;
            if (_storageWarmers.TryGetValue(accountAddress, out storageWarmer))
            {
                QueueSlotJob(storageWarmer, in index);
                return;
            }

            AcceptJob();
        }

        Hash256 storageRoot;
        try
        {
            storageRoot = _readOnlySnapshotBundle.GetAccount(accountAddress)?.StorageRoot ?? Keccak.EmptyTreeHash;
        }
        catch
        {
            CompleteJob();
            throw;
        }

        lock (_lifetimeLock)
        {
            if (_isDisposing || storageRoot == Keccak.EmptyTreeHash)
            {
                CompleteJobUnderLock();
                return;
            }

            if (!_storageWarmers.TryGetValue(accountAddress, out storageWarmer))
            {
                storageWarmer = new StorageWarmer(this, accountAddress.ToAccountPath.ToHash256(), storageRoot, _logManager);
                _storageWarmers.Add(accountAddress, storageWarmer);
            }

            QueueReservedSlotJob(storageWarmer, in index);
        }
    }

    private void QueueSlotJob(StorageWarmer storageWarmer, in UInt256 index)
    {
        AcceptJob();
        QueueReservedSlotJob(storageWarmer, in index);
    }

    private void QueueReservedSlotJob(StorageWarmer storageWarmer, in UInt256 index)
    {
        bool accepted = false;
        try
        {
            accepted = _trieWarmer.PushSlotJobMpmc(storageWarmer, in index, sequenceId: 0);
        }
        finally
        {
            if (!accepted) CompleteJobUnderLock();
        }
    }

    private void AcceptJob()
    {
        if (_acceptedJobs++ == 0)
        {
            _jobsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
            CompleteJobUnderLock();
        }
    }

    private void CompleteJobUnderLock()
    {
        if (--_acceptedJobs == 0) _jobsDrained!.SetResult();
    }

    private TrieNode FindNodeOrUnknown(Hash256? address, in TreePath path, Hash256 hash)
    {
        TrieNode node = address is null
            ? _snapshotBundle.FindStateNodeOrUnknownForTrieWarmer(in path, hash)
            : _snapshotBundle.FindStorageNodeOrUnknownTrieWarmer(address, in path, hash);
        return node.Keccak != hash
            ? throw new NodeHashMismatchException($"Node hash mismatch. Address {address}. Path: {path}. Hash: {node.Keccak} vs Requested: {hash}")
            : node;
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
                    if (jobsDrained is not null)
                    {
                        OnWaitingForJobs?.Invoke();
                        jobsDrained.GetAwaiter().GetResult();
                    }
                }
                catch (Exception exception)
                {
                    disposeException = ExceptionDispatchInfo.Capture(exception);
                }

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

    private sealed class StateResolver(FlatTrieWarmupSession session) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
            session.FindNodeOrUnknown(address: null, in path, hash);

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            session._readOnlySnapshotBundle.TryLoadStateRlp(in path, hash, flags);

        public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) =>
            address is null ? this : new StorageResolver(session, address);
    }

    private sealed class StorageResolver(FlatTrieWarmupSession session, Hash256AsKey address) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
            session.FindNodeOrUnknown(address, in path, hash);

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            session._readOnlySnapshotBundle.TryLoadStorageRlp(address, in path, hash, flags);
    }

    private sealed class StorageWarmer
        (FlatTrieWarmupSession session, Hash256 addressHash, Hash256 storageRoot, ILogManager logManager)
        : ITrieWarmer.IStorageWarmer
    {
        private readonly StorageTree _storageTree = new(new StorageResolver(session, addressHash), storageRoot, logManager)
        {
            RootHash = storageRoot
        };

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
                session.CompleteJob();
            }
        }
    }
}
