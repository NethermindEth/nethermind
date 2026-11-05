// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State;

internal sealed partial class PersistentStorageProvider
{
    public void SetBackendScope(IWorldStateScopeProvider.IScope? scope) => _currentScope = scope;

    private partial bool HasCapturedOriginals
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _originalValues.Count != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private partial void CaptureOriginalValue(in StorageCell cell, in UInt256 value)
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
        _lastCapturedCell = cell;
        _lastCapturedOriginal = slot;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private partial void EnsureOriginalsIndexed() { }

    // The guest captures every original straight into the map, so the map already answers.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private partial bool TryGetRoundOriginal(in StorageCell storageCell, out UInt256 value)
    {
        value = default;
        return false;
    }

    private partial bool SaveChangeUnlessRoundOriginal(in Change change, bool hasStorageClears)
    {
        if (_originalValues.TryGetValue(change.StorageCell, out UInt256 initialValue)
            && initialValue == change.Value && (!hasStorageClears || !WasCleared(change.StorageCell.Address)))
        {
            return false;
        }

        GetOrCreateStorage(change.StorageCell.Address).SaveChange(change.StorageCell, change.Value);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private partial void EndPendingOriginalsRound() { }

    private partial void UpdateRootHashes(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch) =>
        UpdateRootHashesSingleThread(writeBatch);

    private sealed partial class PerContractState
    {
        /// <remarks>
        /// ILC block-copies each 104-byte pair through corelib's out-of-line Memmove several times per entry, so
        /// the guest reaches each entry's key and value in place instead. The host keeps the pair walk: its dictionary has
        /// no in-place enumerator accessors.
        /// </remarks>
        [SkipLocalsInit]
        private partial (int writes, int skipped) WriteChanges(IWorldStateScopeProvider.IStorageWriteBatch storageWriteBatch)
        {
            int writes = 0;
            int skipped = 0;

            // Deletes are likely rare, so start with zero capacity; the pooled array is rented only on first Add.
            using ArrayPoolListRef<UInt256> deferredDeletes = new(0);

            OptimizedDictionary<SlotKey, StorageChangeTrace>.Enumerator entries = BlockChange.GetEnumerator();
            while (entries.MoveNext())
            {
                ref readonly UInt256 key = ref entries.CurrentKey.Index;
                ref StorageChangeTrace change = ref entries.CurrentValue;
                if (!change.IsPendingWrite)
                {
                    skipped++;
                }
                else if (CommitAndWriteUnlessDelete(in key, ref change, storageWriteBatch))
                {
                    writes++;
                }
                else
                {
                    deferredDeletes.Add(key);
                }
            }

            writes += WriteDeletes(deferredDeletes.AsSpan(), storageWriteBatch);
            return (writes, skipped);
        }

        private static partial class Pool
        {
            private static ConcurrentQueue<PerContractState> _pool => field ??= [];
        }
    }
}
