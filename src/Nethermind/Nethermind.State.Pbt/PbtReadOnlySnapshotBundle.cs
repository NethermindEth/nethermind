// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

/// <summary>An immutable canonical state view composed from snapshot diffs over one persistence snapshot.</summary>
public sealed class PbtReadOnlySnapshotBundle(
    PbtSnapshotPooledList snapshots,
    IPbtPersistence.IReader reader) : RefCountingDisposable
{
    private bool _isDisposed;

    public ValueHash256 TreeRoot
    {
        get
        {
            GuardDispose();
            return snapshots.Count > 0 ? snapshots[^1].TreeRoot : reader.CurrentRoot;
        }
    }

    internal RefCountingMemory? GetNodeGroup(IPbtNodePath groupKey)
    {
        GuardDispose();
        ArgumentNullException.ThrowIfNull(groupKey);
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
            throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));
        for (int index = snapshots.Count - 1; index >= 0; index--)
            if (snapshots[index].Content.TryGetNodeGroup(groupKey, out RefCountingMemory? payload)) return payload;
        return reader.GetNodeGroup(groupKey);
    }

    internal ulong GetCodeReference(in ValueHash256 codeHash)
    {
        GuardDispose();
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.TryGetCodeReference(codeHash, out ulong? count)) return count ?? 0;
        }

        return reader.GetCodeReference(codeHash);
    }

    internal IEnumerable<KeyValuePair<ValueHash256, Account>> EnumerateAccounts()
    {
        GuardDispose();
        Dictionary<ValueHash256, Account?> visible = [];
        foreach ((ValueHash256 hash, Account account) in reader.EnumerateAccounts()) visible[hash] = account;
        foreach (PbtSnapshot snapshot in snapshots)
            foreach ((ValueHash256 hash, Account? account) in snapshot.Content.Accounts) visible[hash] = account;
        foreach ((ValueHash256 hash, Account? account) in visible)
            if (account is not null) yield return new(hash, account);
    }

    internal IEnumerable<KeyValuePair<PbtStorageFullKey, EvmWord>> EnumerateStorage(ValueHash256? addressFilter = null)
    {
        GuardDispose();
        SortedDictionary<PbtStorageFullKey, EvmWord> visible = [];
        if (addressFilter is null)
        {
            foreach ((PbtStorageFullKey key, EvmWord value) in reader.EnumerateStorage()) visible[key] = value;
        }
        else
        {
            byte[] prefix = new byte[1 + ValueHash256.MemorySize];
            addressFilter.Value.Bytes.CopyTo(prefix.AsSpan(1));
            foreach (byte zone in new[] { Eip8297KeyDerivation.AccountZone, Eip8297KeyDerivation.StorageZone })
            {
                prefix[0] = zone;
                foreach ((PbtStorageFullKey key, EvmWord value) in reader.EnumerateStorage(new PbtStorageFullKey(prefix))) visible[key] = value;
            }
        }
        foreach (PbtSnapshot snapshot in snapshots) PbtFlatState.ApplyStorage(visible, snapshot.Content, addressFilter);
        foreach ((PbtStorageFullKey key, EvmWord value) in visible)
            if (!EvmWordSlot.IsZero(value)) yield return new(key, value);
    }

    internal IEnumerable<KeyValuePair<PbtStorageFullKey, ValueHash256>> EnumerateLeaves() =>
        PbtFlatState.EnumerateLeaves(EnumerateAccounts(), EnumerateStorage(), hash => GetCode(hash));

    internal IEnumerable<KeyValuePair<PbtStorageFullKey, ValueHash256>> EnumerateLeaves(PbtStorageFullKey prefix)
    {
        foreach (KeyValuePair<PbtStorageFullKey, ValueHash256> leaf in EnumerateLeaves())
            if (prefix.IsPrefixOf(leaf.Key)) yield return leaf;
    }

    public Account? GetAccount(Address address) => GetAccount(PbtKeyDerivation.AddressKeyHash(address));

    internal Account? GetAccount(in ValueHash256 addressHash)
    {
        GuardDispose();
        for (int index = snapshots.Count - 1; index >= 0; index--)
            if (snapshots[index].Content.Accounts.TryGetValue(addressHash, out Account? account)) return account;
        return reader.GetAccount(addressHash);
    }

    public EvmWord GetSlot(Address address, in UInt256 slot) => GetSlot(PbtStateKey.Storage(address, slot));

    internal EvmWord GetSlot(PbtStorageFullKey key)
    {
        GuardDispose();
        ValueHash256 addressHash = PbtFlatState.StorageAddress(key);
        for (int index = snapshots.Count - 1; index >= 0; index--)
        {
            PbtSnapshotContent content = snapshots[index].Content;
            if (content.Storages.TryGetValue(key, out EvmWord value)) return value;
            if (content.SelfDestructedStorageAddresses.ContainsKey(addressHash)) return default;
        }
        return reader.GetSlot(key);
    }

    internal CodeInfo? GetCode(in ValueHash256 codeHash)
    {
        GuardDispose();
        for (int index = snapshots.Count - 1; index >= 0; index--)
            if (snapshots[index].Content.Codes.TryGetValue(codeHash, out CodeInfo? code)) return code;
        return reader.GetCode(codeHash);
    }

    public bool TryLease() => TryAcquireLease();

    protected override void CleanUp()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        try
        {
            snapshots.Dispose();
        }
        finally
        {
            reader.Dispose();
        }
    }

    private void GuardDispose() => ObjectDisposedException.ThrowIf(_isDisposed, this);
}
