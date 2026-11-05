// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.Collections;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Flat;

/// <summary>The sorted, pooled counterpart of <see cref="SnapshotContent"/> used for compacted snapshots.</summary>
public sealed class SortedSnapshotContent : IDisposable, IResettable
{
    private readonly SortedMergeDictionary<HashedKey<Address>, Account?> _accounts = new();
    private readonly SortedMergeDictionary<HashedKey<(Address, UInt256)>, UInt256?> _storages = new();
    private readonly SortedMergeDictionary<HashedKey<Address>, bool> _selfDestructedStorageAddresses = new();

    internal SortedMergeDictionary<HashedKey<Address>, Account?> SortedAccounts => _accounts;
    internal SortedMergeDictionary<HashedKey<(Address, UInt256)>, UInt256?> SortedStorages => _storages;
    internal SortedMergeDictionary<HashedKey<Address>, bool> SortedSelfDestructs => _selfDestructedStorageAddresses;

    internal ulong StateRootPointer;
    internal readonly Dictionary<Hash256, ulong> StorageRoots = [];

    public IEnumerable<KeyValuePair<HashedKey<Address>, Account?>> Accounts => _accounts;
    public IEnumerable<KeyValuePair<HashedKey<Address>, bool>> SelfDestructedStorageAddresses => _selfDestructedStorageAddresses;
    public IEnumerable<KeyValuePair<HashedKey<(Address, UInt256)>, UInt256?>> Storages => _storages;

    public int AccountsCount => _accounts.Count;
    public int StoragesCount => _storages.Count;

    public bool TryGetAccount(HashedKey<Address> key, out Account? acc) => _accounts.TryGetValue(key, out acc);
    public bool HasSelfDestruct(HashedKey<Address> key) => _selfDestructedStorageAddresses.TryGetValue(key, out bool _);
    public bool TryGetStorage(HashedKey<(Address, UInt256)> key, out UInt256? value) => _storages.TryGetValue(key, out value);

    public long EstimateMemory() => CaptureCounts().EstimateMemory();

    /// <inheritdoc cref="SnapshotContentCounts.EstimateCompactedMemory"/>
    public long EstimateCompactedMemory() => CaptureCounts().EstimateCompactedMemory();

    private SnapshotContentCounts CaptureCounts() => new(
        _accounts.Count,
        _storages.Count,
        _selfDestructedStorageAddresses.Count,
        StateRootPointer != 0 ? 1 : 0,
        StorageRoots.Count);

    public void Reset()
    {
        _accounts.NoResizeClear();
        _storages.NoResizeClear();
        _selfDestructedStorageAddresses.NoResizeClear();
        StateRootPointer = 0;
        StorageRoots.Clear();
    }

    public void Dispose()
    {
        _accounts.Dispose();
        _storages.Dispose();
        _selfDestructedStorageAddresses.Dispose();
    }
}
