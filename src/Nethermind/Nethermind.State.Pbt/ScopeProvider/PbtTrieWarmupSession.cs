// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Metric;
using Nethermind.Core.Utils;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat.ScopeProvider;

namespace Nethermind.State.Pbt.ScopeProvider;

internal sealed class PbtTrieWarmupSession(
    PbtSnapshotPooledList initialSnapshots,
    PbtReadOnlySnapshotBundle readOnlyBundle,
    PbtTransientResource transientResource,
    ITrieWarmer trieWarmer,
    int sequenceId,
    IPbtTrieNodeCache trieNodeCache,
    long minSubtreeBytes,
    ILogger logger) : IWorldStateScopeProvider.ITrieWarmupSession, ITrieWarmer.IAddressWarmer, IPbtStore
{
    private static readonly StringLabel _addressJobLabel = new(Metrics.TrieWarmerAddressKind);
    private static readonly StringLabel _storageJobLabel = new(Metrics.TrieWarmerStorageKind);
    private readonly ConcurrentDictionary<AddressAsKey, StorageWarmer> _storageWarmers = [];
    private readonly PbtPinnedGroups _pinnedGroups = new();
    // The owner's lease, each borrow and each in-flight warm-up hold one count; the last to leave releases the frozen layers.
    private long _accessors = RefCountingLease.Single;
    private bool _isStopped;
    private int _droppedLogged;
    // Detailed observers stay no-ops unless Metrics.EnableDetailedMetric, so the timestamps are skipped with them.
    private readonly bool _recordJobTimes = Metrics.PbtTrieWarmerJobTime is not NoopMetricObserver;

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
            if (!ShouldPrewarm(in address, null)) return;
            Address hinted = address.ToAddress();
            if (!trieWarmer.PushAddressJob(this, hinted, sequenceId)) ReportDropped(hinted);
        }
        finally
        {
            ExitOperation();
        }
    }

    public void HintWarmSlot(in ValueAddress address, in UInt256 index) => HintWarmSlot(in address, warmerKey: null, in index, singleProducer: false);

    /// <param name="warmerKey">The address as an object, when the caller already holds one, so keying the warmer allocates nothing.</param>
    internal void HintWarmSlot(in ValueAddress address, Address? warmerKey, in UInt256 index, bool singleProducer)
    {
        if (!TryEnterOperation(sequenceId)) return;
        try
        {
            if (!ShouldPrewarm(in address, index)) return;
            StorageWarmer storageWarmer = _storageWarmers.GetOrAdd(warmerKey ?? address.ToAddress(), static (key, session) => new StorageWarmer(session, key.Value), this);
            if (!singleProducer || !trieWarmer.PushSlotJob(storageWarmer, in index, sequenceId))
                trieWarmer.PushSlotJobMpmc(storageWarmer, in index, sequenceId);
        }
        finally
        {
            ExitOperation();
        }
    }

    // The dedup filter already holds the address, so this scope cannot warm it later. One line per session keeps a
    // full queue from flooding the log; the counter carries the volume.
    private void ReportDropped(Address address)
    {
        Metrics.PbtTrieWarmerDropped.Increment(Metrics.TrieWarmerAddressKind);
        if (Interlocked.Exchange(ref _droppedLogged, 1) == 0 && logger.IsWarn)
            logger.Warn($"Pbt trie warmer queue refused the warm-up of {address}; further drops in this scope are only counted in {nameof(Metrics.PbtTrieWarmerDropped)}.");
    }

    private bool ShouldPrewarm(in ValueAddress address, UInt256? index)
    {
        string kind = index is null ? Metrics.TrieWarmerAddressKind : Metrics.TrieWarmerStorageKind;
        if (transientResource.ShouldPrewarm(in address, index))
        {
            Metrics.PbtTrieWarmerTriggered.Increment(kind);
            return true;
        }
        Metrics.PbtTrieWarmerSkippedByDeduplication.Increment(kind);
        return false;
    }

    public bool WarmUpStateTrie(Address address, int jobSequenceId)
    {
        PbtStorageTreeKey key = (PbtStorageTreeKey)PbtStateKey.Account(address, PbtKeyDerivation.BasicDataLeafKey);
        return WarmUpPath(key, jobSequenceId, Metrics.TrieWarmerAddressKind, _addressJobLabel);
    }

    private bool WarmUpPath(in PbtStorageTreeKey key, int jobSequenceId, string kind, StringLabel jobLabel)
    {
        if (!TryEnterOperation(jobSequenceId))
        {
            Metrics.PbtTrieWarmerStale.Increment(kind);
            return false;
        }
        try
        {
            long start = _recordJobTimes ? Stopwatch.GetTimestamp() : 0;
            if (PbtTrieWarmer.WarmUpPath(this, TreeRoot, key, minSubtreeBytes, _pinnedGroups)) Metrics.IncrementPbtTrieWarmerStoppedBySmallSubtree();
            if (_recordJobTimes) Metrics.PbtTrieWarmerJobTime.Observe(Stopwatch.GetTimestamp() - start, jobLabel);
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

    // Only frozen, independently leased layers are read; live write buffers and growing snapshot lists never are.
    // Whatever is resolved past them is staged in the transient resource so the fold finds it without repeating the read.
    RefCountingMemory? IPbtStore.GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash)
    {
        PbtStorageNodePath storagePath = groupKey.ToPath<PbtStorageNodePath>();
        for (int index = initialSnapshots.Count - 1; index >= 0; index--)
            if (initialSnapshots[index].Content.TryGetNodeGroup(storagePath, out RefCountingMemory? payload)) return payload;
        if (transientResource.NodeGroups.TryGet(groupHash, storagePath, out RefCountingMemory? staged)) return staged;
        if (!trieNodeCache.TryGet(groupHash, storagePath, out RefCountingMemory? result)) result = readOnlyBundle.GetNodeGroup(storagePath);
        if (result is not null) transientResource.NodeGroups.Set(groupHash, storagePath, result);
        return result;
    }

    IPbtConcurrentWriter IPbtStore.CreateWriter() => throw new NotSupportedException();

    public void Dispose() => ExitOperation();

    private void ReleaseFrozenLayers()
    {
        try
        {
            _pinnedGroups.Dispose();
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

        public bool WarmUpStorageTrie(UInt256 index, int jobSequenceId) => session.WarmUpPath(PbtStateKey.Storage(address, _addressHash, index), jobSequenceId, Metrics.TrieWarmerStorageKind, _storageJobLabel);
    }
}
