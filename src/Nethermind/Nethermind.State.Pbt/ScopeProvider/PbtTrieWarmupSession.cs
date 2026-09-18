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
    // The owner's lease, each borrow and each in-flight warm-up hold one count; the last to leave releases the frozen layers.
    private long _accessors = RefCountingLease.Single;
    private bool _isStopped;

    internal ValueHash256 TreeRoot { get; } = initialSnapshots.Count > 0 ? initialSnapshots[^1].TreeRoot : readOnlyBundle.TreeRoot;
    internal bool IsStopped => Volatile.Read(ref _isStopped);

    internal bool TryAcquireLease() => RefCountingLease.TryAcquire(ref _accessors);

    internal void AcquireLease()
    {
        if (!TryAcquireLease()) throw new ObjectDisposedException(nameof(PbtTrieWarmupSession));
    }

    /// <summary>Refuses further warm-ups without waiting for those in flight, which keep the frozen layers until they exit.</summary>
    internal void StopWarming() => Volatile.Write(ref _isStopped, true);

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
        PbtTreeKey key = (PbtTreeKey)PbtStateKey.Account(address, PbtKeyDerivation.BasicDataLeafKey);
        return WarmUpPath(key, jobSequenceId);
    }

    private bool WarmUpPath(in PbtTreeKey key, int jobSequenceId)
    {
        if (!TryEnterOperation(jobSequenceId)) return false;
        try
        {
            PbtTrieWarmer.WarmUpPath(this, TreeRoot, key);
            return true;
        }
        finally
        {
            ExitOperation();
        }
    }

    private bool TryEnterOperation(int jobSequenceId)
    {
        if (IsStopped || jobSequenceId != sequenceId || !RefCountingLease.TryAcquire(ref _accessors)) return false;
        if (!IsStopped) return true;
        ExitOperation();
        return false;
    }

    private void ExitOperation()
    {
        if (RefCountingLease.ReleaseOnce(ref _accessors)) ReleaseFrozenLayers();
    }

    // Only frozen, independently leased layers participate; live write buffers and growing snapshot lists never do.
    RefCountingMemory? IPbtStore.GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash)
    {
        PbtStorageNodePath storagePath = groupKey.ToPath<PbtStorageNodePath>();
        for (int index = initialSnapshots.Count - 1; index >= 0; index--)
            if (initialSnapshots[index].Content.TryGetNodeGroup(storagePath, out RefCountingMemory? payload)) return payload;
        if (trieNodeCache?.TryGet(groupHash, storagePath, out RefCountingMemory? cached) == true) return cached;
        RefCountingMemory? result = readOnlyBundle.GetNodeGroup(storagePath);
        if (result is not null) trieNodeCache?.Add(groupHash, storagePath, result);
        return result;
    }

    void IPbtStore.SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload) => throw new NotSupportedException();

    public void Dispose() => ExitOperation();

    private void ReleaseFrozenLayers()
    {
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
        private readonly ValueHash256 _addressHash = PbtKeyDerivation.AddressKeyHash(address);

        public bool WarmUpStorageTrie(UInt256 index, int jobSequenceId) => session.WarmUpPath(PbtStateKey.Storage(address, _addressHash, index), jobSequenceId);
    }
}
