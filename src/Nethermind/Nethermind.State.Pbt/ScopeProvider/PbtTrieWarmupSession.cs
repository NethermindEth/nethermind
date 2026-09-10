// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Flat.ScopeProvider;

namespace Nethermind.State.Pbt.ScopeProvider;

internal sealed class PbtTrieWarmupSession(
    PbtSnapshotPooledList initialSnapshots,
    PbtReadOnlySnapshotBundle readOnlyBundle,
    PbtTransientResource transientResource,
    ITrieWarmer trieWarmer,
    int sequenceId,
    PbtTrieNodeCache? trieNodeCache) : IWorldStateScopeProvider.ITrieWarmupSession, ITrieWarmer.IAddressWarmer, IPbtStore
{
    private readonly ConcurrentDictionary<AddressAsKey, StorageWarmer> _storageWarmers = [];
    private long _leases = RefCountingLease.Single;
    private long _operations = RefCountingLease.Single;
    private bool _isStopped;

    internal ValueHash256 TreeRoot { get; } = initialSnapshots.Count > 0 ? initialSnapshots[^1].TreeRoot : readOnlyBundle.TreeRoot;
    internal bool IsStopped => Volatile.Read(ref _isStopped);

    internal void AcquireLease()
    {
        if (!RefCountingLease.TryAcquire(ref _leases)) throw new ObjectDisposedException(nameof(PbtTrieWarmupSession));
    }

    internal void StopWarming()
    {
        if (!Interlocked.Exchange(ref _isStopped, true)) RefCountingLease.ReleaseOnce(ref _operations);
        SpinWait spinWait = default;
        while (Volatile.Read(ref _operations) > RefCountingLease.NoAccessors) spinWait.SpinOnce();
    }

    public void HintWarmAccount(in ValueAddress address)
    {
        if (!TryEnterOperation(sequenceId)) return;
        try
        {
            if (ShouldPrewarm(in address, null)) trieWarmer.PushAddressJob(this, address.ToAddress(), sequenceId);
        }
        finally
        {
            ExitOperation();
        }
    }

    public void HintWarmSlot(in ValueAddress address, in UInt256 index) => HintWarmSlot(in address, in index, singleProducer: false);

    internal void HintWarmSlot(in ValueAddress address, in UInt256 index, bool singleProducer)
    {
        if (!TryEnterOperation(sequenceId)) return;
        try
        {
            if (!ShouldPrewarm(in address, index)) return;
            StorageWarmer storageWarmer = _storageWarmers.GetOrAdd(address.ToAddress(), static (key, session) => new StorageWarmer(session, key.Value), this);
            if (!singleProducer || !trieWarmer.PushSlotJob(storageWarmer, in index, sequenceId))
                trieWarmer.PushSlotJobMpmc(storageWarmer, in index, sequenceId);
        }
        finally
        {
            ExitOperation();
        }
    }

    private bool ShouldPrewarm(in ValueAddress address, UInt256? index)
    {
        if (transientResource.ShouldPrewarm(in address, index))
        {
            Metrics.IncrementPbtTrieWarmerTriggered();
            return true;
        }
        Metrics.IncrementPbtTrieWarmerSkippedByDeduplication();
        return false;
    }

    public bool WarmUpStateTrie(Address address, int jobSequenceId)
    {
        PbtStorageFullKey key = (PbtStorageFullKey)PbtStateKey.Account(address, PbtKeyDerivation.BasicDataLeafKey);
        return WarmUpPath(key, jobSequenceId);
    }

    private bool WarmUpPath(in PbtStorageFullKey key, int jobSequenceId)
    {
        if (!TryEnterOperation(jobSequenceId)) return false;
        try
        {
            PbtTrieWarmer.WarmUpPath(this, key);
            return true;
        }
        finally
        {
            ExitOperation();
        }
    }

    private bool TryEnterOperation(int jobSequenceId)
    {
        if (IsStopped || jobSequenceId != sequenceId || !RefCountingLease.TryAcquire(ref _operations)) return false;
        if (!IsStopped) return true;
        ExitOperation();
        return false;
    }

    private void ExitOperation() => RefCountingLease.ReleaseOnce(ref _operations);

    // Only frozen, independently leased layers participate; live write buffers and growing snapshot lists never do.
    RefCountingMemory? IPbtStore.GetNodeGroup<TPath>(TPath groupKey) where TPath : struct
    {
        for (int index = initialSnapshots.Count - 1; index >= 0; index--)
            if (initialSnapshots[index].Content.TryGetNodeGroup(groupKey, out RefCountingMemory? payload)) return payload;
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
            throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));
        ValueHash256 root = readOnlyBundle.TreeRoot;
        if (trieNodeCache?.TryGet(root, groupKey, out RefCountingMemory? cached) == true) return cached;
        RefCountingMemory? result = readOnlyBundle.GetNodeGroup(groupKey);
        if (result is not null) trieNodeCache?.Add(root, groupKey, result);
        return result;
    }

    void IPbtStore.SetNodeGroup<TPath>(TPath groupKey, RefCountingMemory? payload) where TPath : struct => throw new NotSupportedException();

    public void Dispose()
    {
        if (!RefCountingLease.ReleaseOnce(ref _leases)) return;
        StopWarming();
        try
        {
            transientResource.ReleaseLease();
        }
        finally
        {
            try
            {
                initialSnapshots.Dispose();
            }
            finally
            {
                readOnlyBundle.Dispose();
            }
        }
    }

    private sealed class StorageWarmer(PbtTrieWarmupSession session, Address address) : ITrieWarmer.IStorageWarmer
    {
        public bool WarmUpStorageTrie(UInt256 index, int jobSequenceId) => session.WarmUpPath(PbtStateKey.Storage(address, index), jobSequenceId);
    }
}
