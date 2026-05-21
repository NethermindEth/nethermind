// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Per-account changes recorded at a single BAL index (i.e. one transaction). Each scalar field
/// (balance / nonce / code) collapses to a single optional value because at one index there can
/// only be one final value. Storage changes carry one <see cref="StorageChange"/> per slot.
/// </summary>
public class AccountChangesAtIndex(Address address)
{
    public Address Address { get; private set; } = address;

    public BalanceChange? BalanceChange { get; internal set; }
    public NonceChange? NonceChange { get; internal set; }
    public CodeChange? CodeChange { get; internal set; }

    public UInt256? PreTxBalance { get; internal set; }
    public byte[]? PreTxCode { get; internal set; }
    private Dictionary<UInt256, UInt256>? _preTxStorage;

    private readonly Dictionary<UInt256, StorageChange> _storageChanges = new(GenericEqualityComparer.GetOptimized<UInt256>());
    private readonly HashSet<UInt256> _storageReads = new(GenericEqualityComparer.GetOptimized<UInt256>());

    public Dictionary<UInt256, StorageChange>.KeyCollection ChangedSlots => _storageChanges.Keys;
    public Dictionary<UInt256, StorageChange> StorageChanges => _storageChanges;
    public int StorageChangeCount => _storageChanges.Count;
    public HashSet<UInt256> StorageReads => _storageReads;

    public bool HasStorageChange(UInt256 key) => _storageChanges.ContainsKey(key);

    public bool TryGetStorageChange(UInt256 key, [NotNullWhen(true)] out StorageChange? storageChange)
    {
        if (_storageChanges.TryGetValue(key, out StorageChange existing))
        {
            storageChange = existing;
            return true;
        }
        storageChange = null;
        return false;
    }

    public void SetStorageChange(UInt256 key, StorageChange storageChange)
        => _storageChanges[key] = storageChange;

    public bool RemoveStorageChange(UInt256 key) => _storageChanges.Remove(key);

    public bool TryRemoveStorageChange(UInt256 key, [NotNullWhen(true)] out StorageChange? storageChange)
    {
        if (_storageChanges.Remove(key, out StorageChange existing))
        {
            storageChange = existing;
            return true;
        }
        storageChange = null;
        return false;
    }

    public void AddStorageRead(UInt256 key) => _storageReads.Add(key);

    public bool RemoveStorageRead(UInt256 key) => _storageReads.Remove(key);

    public UInt256 GetOrCapturePreTxStorage(UInt256 key, in UInt256 captureValue)
    {
        _preTxStorage ??= new Dictionary<UInt256, UInt256>(8);
        ref UInt256 slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_preTxStorage, key, out bool exists);
        if (!exists) slot = captureValue;
        return slot;
    }

    public void ClearStorage()
    {
        _storageChanges.Clear();
        _storageReads.Clear();
        _preTxStorage?.Clear();
    }

    public void Reset(Address address)
    {
        Address = address;
        BalanceChange = null;
        NonceChange = null;
        CodeChange = null;
        PreTxBalance = null;
        PreTxCode = null;
        _preTxStorage?.Clear();
        _storageChanges.Clear();
        _storageReads.Clear();
    }
}
