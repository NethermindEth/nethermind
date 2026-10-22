// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Cpu;
using Nethermind.Core.Threading;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State;

internal sealed partial class PersistentStorageProvider
{
    private static readonly ArrayPool<KeyValuePair<StorageCell, UInt256>> PendingOriginalsPool =
        ArrayPool<KeyValuePair<StorageCell, UInt256>>.Create(LargeMapPool<StorageCell, UInt256>.MaxRetainedCapacity, 1);
    private ArrayPoolList<KeyValuePair<StorageCell, UInt256>>? _pendingOriginalValues;
    private bool _originalsIndexed;
    // The pool serves a rent from up to two sizes above the one asked for, so a list this large may never have grown.
    internal const int RetainedPendingCapacity = PooledDictionaryCapacity * 4;

    public void SetBackendScope(IWorldStateScopeProvider.IScope? scope)
    {
        _currentScope = scope;
        // A list within RetainedPendingCapacity is kept: the pool shared by every provider serves only a few overlapping
        // scopes, so handing it back makes concurrent ones, the prewarmer's, allocate their own.
        if (scope is null && _pendingOriginalValues is { Capacity: > RetainedPendingCapacity })
        {
            _pendingOriginalValues.Dispose();
            _pendingOriginalValues = null;
        }
    }

    private partial bool HasCapturedOriginals => _originalValues.Count != 0 || _pendingOriginalValues is { Count: > 0 };

    // The pending originals are indexed lazily, so a slot's round original is read from its block change while the
    // round has no storage clear; the originals map stays the fallback.
    private partial bool TryGetRoundOriginal(in StorageCell storageCell, out UInt256 value)
    {
        Address address = storageCell.Address;
        if (_storageClearJournal.Count != 0 || (_destroyedThisRound.Count != 0 && _destroyedThisRound.Contains(address))
            || !TryGetStorage(address, out PerContractState? storage))
        {
            value = default;
            return false;
        }

        return storage.TryGetRoundOriginal(in storageCell.Index, _originalsRound, out value);
    }

    private bool TryGetStorage(Address address, [NotNullWhen(true)] out PerContractState? storage)
    {
        if (TryGetRememberedStorage(address, out storage)) return true;
        if (!_storages.TryGetValue(address, out storage)) return false;

        RememberStorage(address, storage);
        return true;
    }

    private partial bool SaveChangeUnlessRoundOriginal(in Change change, bool hasStorageClears)
    {
        if (!hasStorageClears)
        {
            return GetOrCreateStorage(change.StorageCell.Address)
                .SaveChangeUnlessRoundOriginal(in change.StorageCell, in change.Value, _originalsRound);
        }

        if (_originalValues.TryGetValue(change.StorageCell, out UInt256 initialValue)
            && initialValue == change.Value && !WasCleared(change.StorageCell.Address))
        {
            return false;
        }

        GetOrCreateStorage(change.StorageCell.Address).SaveChange(change.StorageCell, change.Value);
        return true;
    }

    private partial void CaptureOriginalValue(in StorageCell cell, in UInt256 value)
    {
        _lastCapturedCell = cell;
        if (!_originalsIndexed)
        {
            (_pendingOriginalValues ??= RentPendingOriginals()).Add(new KeyValuePair<StorageCell, UInt256>(cell, value));
            _lastCapturedOriginal = value;
            return;
        }

        _lastCapturedOriginal = CaptureIndexedOriginalValue(in cell, in value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ArrayPoolList<KeyValuePair<StorageCell, UInt256>> RentPendingOriginals()
    {
        ArrayPoolList<KeyValuePair<StorageCell, UInt256>> list = new(PendingOriginalsPool, PooledDictionaryCapacity);
        // A kept list is dropped with its provider rather than disposed, which the debug build would report as a leak.
        GC.SuppressFinalize(list);
        return list;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private partial void EnsureOriginalsIndexed()
    {
        if (!_originalsIndexed) IndexPendingOriginals();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void IndexPendingOriginals()
    {
        if (_pendingOriginalValues is { } pending)
        {
            foreach (KeyValuePair<StorageCell, UInt256> entry in pending)
            {
                CaptureIndexedOriginalValue(entry.Key, entry.Value);
            }

            ClearPendingOriginals();
        }

        _originalsIndexed = true;
    }

    private partial void EndPendingOriginalsRound()
    {
        ClearPendingOriginals();
        _originalsIndexed = false;
    }

    private void ClearPendingOriginals()
    {
        if (_pendingOriginalValues is not { } pending) return;
        if (pending.Capacity > Core.Collections.CollectionExtensions.DefaultTrimAboveCapacity)
        {
            pending.Dispose();
            _pendingOriginalValues = null;
        }
        else
        {
            pending.Clear();
        }
    }

    private UInt256 CaptureIndexedOriginalValue(in StorageCell cell, in UInt256 value)
    {
        OptimizedDictionary<StorageCell, UInt256> originals = _originalValues;
        if (originals.Count == originals.Capacity && originals.Capacity > OriginalsGrowIntoLargeAbove && !originals.ContainsKey(cell))
        {
            GrowOriginalsIntoLarge();
        }

        ref UInt256 slot = ref _originalValues.GetValueRefOrAddDefault(cell, out bool exists);
        if (!exists)
        {
            slot = value;
        }
        return slot;
    }

    private partial void UpdateRootHashes(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch)
    {
        if (_toUpdateRoots.Count >= 3)
            UpdateRootHashesMultiThread(writeBatch);
        else
            UpdateRootHashesSingleThread(writeBatch);
    }

    private void UpdateRootHashesMultiThread(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch)
    {
        // We can recalculate the roots in parallel as they are all independent tries
        using ArrayPoolList<(
            AddressAsKey Key, PerContractState ContractState,
            IWorldStateScopeProvider.IStorageWriteBatch WriteBatch
            )> storages = new(_toUpdateRoots.Count);

        foreach (KeyValuePair<AddressAsKey, bool> kv in _toUpdateRoots)
        {
            if (!kv.Value) continue;
            if (!_storages.TryGetValue(kv.Key, out PerContractState? contractState))
            {
                Debug.Fail($"Storage root marked changed for {kv.Key} but no contract state is present");
                continue;
            }
            storages.Add((
                kv.Key,
                contractState,
                writeBatch.CreateStorageWriteBatch(kv.Key, contractState.EstimatedChanges)));
        }

        if (storages.Count == 0) return;

        // Schedule larger changes first to help balance the work
        storages.AsSpan().Sort(static (a, b) => b.ContractState.EstimatedChanges.CompareTo(a.ContractState.EstimatedChanges));

        BlockDiag? diag = BlockDiag.InPhase(BlockDiag.Phase.StorageRoots);
        diag?.Add(BlockDiag.C.ParStorageLoops, 1);
        using ParallelUnbalancedWork.WorkerScope workers = ParallelUnbalancedWork.BeginWorkerScope(Environment.ProcessorCount);
        ParallelUnbalancedWork.For(
            0,
            storages.Count,
            RuntimeInformation.ParallelOptionsLogicalCores,
            (storages, writes: 0, skips: 0, diag),
            static (i, state) =>
            {
                state.diag?.CountWorkItem(BlockDiag.C.ParStorageMain, BlockDiag.C.ParStorageHelper);
                ref (AddressAsKey Key, PerContractState ContractState, IWorldStateScopeProvider.IStorageWriteBatch WriteBatch) kvp = ref state.storages.GetRef(i);
                (int writes, int skipped) = kvp.ContractState.ProcessStorageChanges(kvp.WriteBatch);
                state.writes += writes;
                state.skips += skipped;

                return state;
            },
            (state) => ReportMetrics(state.writes, state.skips)
        );
    }

    private sealed partial class PerContractState
    {
        [SkipLocalsInit]
        private partial (int writes, int skipped) WriteChanges(IWorldStateScopeProvider.IStorageWriteBatch storageWriteBatch)
        {
            int writes = 0;
            int skipped = 0;

            // Deletes are likely rare, so start with zero capacity; the pooled array is rented only on first Add.
            using ArrayPoolListRef<UInt256> deferredDeletes = new(0);

            foreach (KeyValuePair<SlotKey, StorageChangeTrace> kvp in BlockChange)
            {
                if (!kvp.Value.IsPendingWrite)
                {
                    skipped++;
                }
                else if (CommitAndWriteUnlessDelete(kvp.Key, ref BlockChange.GetValueRefOrNullRef(kvp.Key), storageWriteBatch))
                {
                    writes++;
                }
                else
                {
                    deferredDeletes.Add(kvp.Key);
                }
            }

            writes += WriteDeletes(deferredDeletes.AsSpan(), storageWriteBatch);
            return (writes, skipped);
        }

        private static partial class Pool
        {
            private static readonly ConcurrentQueue<PerContractState> _pool = [];
        }
    }
}
