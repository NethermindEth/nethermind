// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Int256;
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

    internal ValueHash256? GetLeaf(PbtFullKey key)
    {
        GuardDispose();
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.TryGetLeaf(key, out ValueHash256? value)) return value;
        }

        return reader.GetLeaf(key);
    }

    internal byte[]? GetNode(PbtFullKey locator)
    {
        GuardDispose();
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.TryGetNode(locator, out byte[]? encoding)) return encoding;
        }

        return reader.GetNode(locator);
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

    internal IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves() => EnumerateLeavesCore(null);

    internal IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves(PbtFullKey prefix) => EnumerateLeavesCore(prefix);

    private IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeavesCore(PbtFullKey? prefix)
    {
        GuardDispose();
        SortedDictionary<PbtFullKey, ValueHash256?> visible = [];
        IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> persisted = prefix is null
            ? reader.EnumerateLeaves()
            : reader.EnumerateLeaves(prefix);
        foreach ((PbtFullKey key, ValueHash256 value) in persisted) visible[key] = value;
        for (int i = 0; i < snapshots.Count; i++)
        {
            foreach ((PbtFullKey key, ValueHash256? value) in snapshots[i].Content.Leaves)
            {
                if (prefix is null || prefix.IsPrefixOf(key)) visible[key] = value;
            }
        }

        foreach ((PbtFullKey key, ValueHash256? value) in visible)
        {
            if (value is not null) yield return new KeyValuePair<PbtFullKey, ValueHash256>(key, value.Value);
        }
    }

    internal IEnumerable<KeyValuePair<PbtFullKey, byte[]>> EnumerateNodes()
    {
        GuardDispose();
        SortedDictionary<PbtFullKey, byte[]?> visible = [];
        foreach ((PbtFullKey locator, byte[] encoding) in reader.EnumerateNodes()) visible[locator] = encoding;
        for (int i = 0; i < snapshots.Count; i++)
        {
            foreach ((PbtFullKey locator, byte[]? encoding) in snapshots[i].Content.Nodes) visible[locator] = encoding;
        }

        foreach ((PbtFullKey locator, byte[]? encoding) in visible)
        {
            if (encoding is not null) yield return new KeyValuePair<PbtFullKey, byte[]>(locator, encoding);
        }
    }

    internal bool AnyLeaf(PbtFullKey prefix)
    {
        foreach (KeyValuePair<PbtFullKey, ValueHash256> _ in EnumerateLeaves(prefix)) return true;
        return false;
    }

    public Account? GetAccount(Address address)
    {
        ValueHash256? basicData = GetLeaf(PbtStateKey.Account(address, PbtKeyDerivation.BasicDataLeafKey));
        ValueHash256? codeHash = GetLeaf(PbtStateKey.Account(address, PbtKeyDerivation.CodeHashLeafKey));
        if (basicData is null && codeHash is null) return null;

        ulong nonce = 0;
        UInt256 balance = default;
        if (basicData is not null) PbtKeyDerivation.UnpackBasicData(basicData.Value.Bytes, out nonce, out balance);
        return new Account(nonce, balance, Keccak.EmptyTreeHash,
            codeHash is null ? Keccak.OfAnEmptyString : new Hash256(codeHash.Value.Bytes));
    }

    public EvmWord GetSlot(Address address, in UInt256 slot)
    {
        ValueHash256? value = GetLeaf(PbtStateKey.Storage(address, slot));
        return value is null ? default : EvmWordSlot.FromStripped(value.Value.Bytes);
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
