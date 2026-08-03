// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>A writable canonical branch over sealed local snapshots and a shared read-only base.</summary>
public sealed class PbtSnapshotBundle(
    PbtSnapshotPooledList snapshots,
    PbtReadOnlySnapshotBundle readOnlyBundle,
    IPbtResourcePool resourcePool,
    PbtResourcePool.Usage usage) : IDisposable
{
    private PbtSnapshotContent? _writeBuffer = resourcePool.GetSnapshotContent(usage);
    private PbtPendingFlatWrites? _pending = resourcePool.GetPendingFlatWrites(usage);
    private bool _isDisposed;

    public ValueHash256 TreeRoot => snapshots.Count > 0 ? snapshots[^1].TreeRoot : readOnlyBundle.TreeRoot;

    private PbtSnapshotContent WriteBuffer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return _writeBuffer!;
        }
    }

    private PbtPendingFlatWrites Pending
    {
        get
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return _pending!;
        }
    }

    internal ValueHash256? GetLeaf(PbtFullKey key)
    {
        if (WriteBuffer.TryGetLeaf(key, out ValueHash256? value)) return value;
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.TryGetLeaf(key, out value)) return value;
        }

        return readOnlyBundle.GetLeaf(key);
    }

    internal void SetLeaf(PbtFullKey key, ValueHash256? value) => WriteBuffer.SetLeaf(key, value);

    internal byte[]? GetNode(PbtFullKey locator)
    {
        if (WriteBuffer.TryGetNode(locator, out byte[]? encoding)) return encoding;
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.TryGetNode(locator, out encoding)) return encoding;
        }

        return readOnlyBundle.GetNode(locator);
    }

    internal ulong GetCodeReference(in ValueHash256 codeHash)
    {
        if (WriteBuffer.TryGetCodeReference(codeHash, out ulong? count)) return count ?? 0;
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.TryGetCodeReference(codeHash, out count)) return count ?? 0;
        }

        return readOnlyBundle.GetCodeReference(codeHash);
    }

    internal void SetCodeReference(in ValueHash256 codeHash, ulong? referenceCount) =>
        WriteBuffer.SetCodeReference(codeHash, referenceCount);

    internal IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves() => EnumerateLeavesCore(null);

    internal IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves(PbtFullKey prefix) => EnumerateLeavesCore(prefix);

    private IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeavesCore(PbtFullKey? prefix)
    {
        SortedDictionary<PbtFullKey, ValueHash256?> visible = [];
        IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> shared = prefix is null
            ? readOnlyBundle.EnumerateLeaves()
            : readOnlyBundle.EnumerateLeaves(prefix);
        foreach ((PbtFullKey key, ValueHash256 value) in shared) visible[key] = value;
        for (int i = 0; i < snapshots.Count; i++) AddLeaves(visible, snapshots[i].Content, prefix);
        AddLeaves(visible, WriteBuffer, prefix);
        foreach ((PbtFullKey key, ValueHash256? value) in visible)
        {
            if (value is not null) yield return new KeyValuePair<PbtFullKey, ValueHash256>(key, value.Value);
        }
    }

    private static void AddLeaves(SortedDictionary<PbtFullKey, ValueHash256?> visible, PbtSnapshotContent content, PbtFullKey? prefix)
    {
        foreach ((PbtFullKey key, ValueHash256? value) in content.Leaves)
        {
            if (prefix is null || prefix.IsPrefixOf(key)) visible[key] = value;
        }
    }

    internal IEnumerable<KeyValuePair<PbtFullKey, byte[]>> EnumerateNodes()
    {
        SortedDictionary<PbtFullKey, byte[]?> visible = [];
        foreach ((PbtFullKey locator, byte[] encoding) in readOnlyBundle.EnumerateNodes()) visible[locator] = encoding;
        for (int i = 0; i < snapshots.Count; i++)
        {
            foreach ((PbtFullKey locator, byte[]? encoding) in snapshots[i].Content.Nodes) visible[locator] = encoding;
        }
        foreach ((PbtFullKey locator, byte[]? encoding) in WriteBuffer.Nodes) visible[locator] = encoding;
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

    internal void DeletePrefix(PbtFullKey prefix)
    {
        foreach ((PbtFullKey key, _) in EnumerateLeaves(prefix)) WriteBuffer.SetLeaf(key, null);
    }

    internal void ReplaceNodes(IReadOnlyList<PbtEncodedNode> nodes)
    {
        SortedDictionary<PbtFullKey, byte[]> replacement = [];
        foreach (PbtEncodedNode node in nodes)
        {
            PbtFullKey locator = new(node.LocatorEncoding.Span);
            replacement[locator] = node.NodeEncoding.ToArray();
        }

        foreach ((PbtFullKey locator, _) in EnumerateNodes())
        {
            if (!replacement.ContainsKey(locator)) WriteBuffer.SetNode(locator, []);
        }
        foreach ((PbtFullKey locator, byte[] encoding) in replacement) WriteBuffer.SetNode(locator, encoding);
    }

    public Account? GetAccount(Address address)
    {
        if (Pending.Accounts.TryGetValue(address, out Account? pending)) return pending;
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
        if (Pending.Slots.TryGetValue((address, slot), out EvmWord pending)) return pending;
        if (Pending.SelfDestructs.ContainsKey(address)) return default;
        ValueHash256? value = GetLeaf(PbtStateKey.Storage(address, slot));
        return value is null ? default : EvmWordSlot.FromStripped(value.Value.Bytes);
    }

    public void SetAccount(Address address, Account? account) => Pending.Accounts[address] = account;

    internal void PromoteAccount(Address address, Account? account) => Pending.Accounts.TryAdd(address, account);

    internal IEnumerable<KeyValuePair<AddressAsKey, Account?>> EnumeratePendingAccounts() => Pending.Accounts;

    public void SetSlot(Address address, in UInt256 slot, in EvmWord value) => Pending.Slots[(address, slot)] = value;

    public void SelfDestruct(Address address)
    {
        foreach (((AddressAsKey Address, UInt256 Slot) key, _) in Pending.Slots)
        {
            if (key.Address.Equals((AddressAsKey)address)) Pending.Slots.TryRemove(key, out _);
        }
        Pending.SelfDestructs[address] = true;
        DeletePrefix(PbtStateKey.StoragePrefix(address));
    }

    public PbtSnapshot CollectSnapshot(in StateId from, in StateId to, in ValueHash256 treeRoot)
    {
        PbtSnapshot snapshot = new(from, to, treeRoot, WriteBuffer, resourcePool, usage);
        snapshot.TryLease();
        snapshots.Add(snapshot);
        _writeBuffer = resourcePool.GetSnapshotContent(usage);
        Pending.Reset();
        return snapshot;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        PbtSnapshotContent? buffer = _writeBuffer;
        _writeBuffer = null;
        PbtPendingFlatWrites? pending = _pending;
        _pending = null;
        try
        {
            snapshots.Dispose();
            if (buffer is not null) resourcePool.ReturnSnapshotContent(usage, buffer);
            if (pending is not null) resourcePool.ReturnPendingFlatWrites(usage, pending);
        }
        finally
        {
            readOnlyBundle.Dispose();
        }
    }
}
