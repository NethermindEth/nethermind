// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Evm.State;

/// <summary>
/// The account and storage values a block committed, recorded by the main processing scope so that
/// <see cref="PreBlockCaches.PrepareFor"/> can replay them into the pre-block caches instead of clearing them.
/// </summary>
/// <remarks>
/// Storage recording is safe from the parallel storage-commit workers; accounts are recorded on the committing
/// thread alone. Replay and <see cref="Clear"/> run only once a block's commit has finished.
/// </remarks>
public sealed class BlockWriteSet
{
    private readonly Dictionary<AddressAsKey, Account?> _accounts = [];
    private readonly Dictionary<AddressAsKey, bool> _preBlockStorage = [];
    private readonly List<(StorageCell Cell, byte[] Value)> _slots = [];
    private readonly List<StorageWipe> _storageWipes = [];
    private readonly Dictionary<AddressAsKey, int> _lastWipeSlotIndex = [];
    private readonly Lock _storageLock = new();

    /// <summary>Records an account's committed value; a later record for the same address wins.</summary>
    public void RecordAccount(Address address, Account? account) => _accounts[address] = account;

    /// <summary>Whether a value (possibly the account's removal) has been recorded for <paramref name="address"/>.</summary>
    public bool HasAccountRecord(Address address) => _accounts.ContainsKey(address);

    /// <summary>
    /// Records whether <paramref name="address"/> held storage before the block, as seen at its first storage access;
    /// later calls for the same address are ignored.
    /// </summary>
    public void RecordPreBlockStorage(Address address, bool hadStorage) => _preBlockStorage.TryAdd(address, hadStorage);

    public bool TryGetPreBlockStorage(Address address, out bool hadStorage) => _preBlockStorage.TryGetValue(address, out hadStorage);

    /// <summary>Records committed slot values in commit order.</summary>
    public void RecordStorage(ReadOnlySpan<(StorageCell Cell, byte[] Value)> writes)
    {
        if (writes.IsEmpty) return;
        lock (_storageLock)
        {
            _slots.AddRange(writes);
        }
    }

    /// <summary>
    /// Records that all storage of <paramref name="address"/> was cleared at this point of the block, superseding the
    /// slots recorded for it so far.
    /// </summary>
    /// <param name="hadStorage">Whether the account held any storage before the block; only then can the caches hold slots of it.</param>
    public void RecordStorageWipe(Address address, bool hadStorage)
    {
        lock (_storageLock)
        {
            _storageWipes.Add(new StorageWipe(address, _slots.Count, hadStorage));
        }
    }

    /// <summary>
    /// Replays the writes into caches holding the pre-block state so that they hold the post-block state.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when a write could not be applied exclusively; the caches are then partly updated and
    /// must be cleared.
    /// </returns>
    internal bool TryApplyTo(SeqlockCache<AddressAsKey, Account> stateCache, SeqlockCache<StorageCell, byte[]> storageCache)
    {
        lock (_storageLock)
        {
            bool skipWipedSlots = _storageWipes.Count > 0;
            if (skipWipedSlots)
            {
                bool storageCleared = false;
                foreach (StorageWipe wipe in _storageWipes)
                {
                    // Pre-block slots of the account cannot be enumerated, so the whole storage cache goes.
                    if (wipe.HadStorage && !storageCleared)
                    {
                        storageCache.Clear();
                        storageCleared = true;
                    }

                    // Wipes are in record order, so the last one for an address wins.
                    _lastWipeSlotIndex[wipe.Address] = wipe.SlotsBefore;
                }
            }

            ReadOnlySpan<(StorageCell Cell, byte[] Value)> slots = CollectionsMarshal.AsSpan(_slots);
            for (int i = 0; i < slots.Length; i++)
            {
                ref readonly (StorageCell Cell, byte[] Value) write = ref slots[i];
                if (skipWipedSlots && _lastWipeSlotIndex.TryGetValue(write.Cell.Address, out int wipedBefore) && i < wipedBefore) continue;
                if (!storageCache.TrySetExclusive(in write.Cell, write.Value)) return false;
            }
        }

        foreach (KeyValuePair<AddressAsKey, Account?> account in _accounts)
        {
            AddressAsKey key = account.Key;
            if (!stateCache.TrySetExclusive(in key, account.Value)) return false;
        }

        return true;
    }

    internal void Clear()
    {
        _accounts.Clear();
        _preBlockStorage.Clear();
        lock (_storageLock)
        {
            _slots.Clear();
            _storageWipes.Clear();
            _lastWipeSlotIndex.Clear();
        }
    }

    private readonly record struct StorageWipe(AddressAsKey Address, int SlotsBefore, bool HadStorage);
}
