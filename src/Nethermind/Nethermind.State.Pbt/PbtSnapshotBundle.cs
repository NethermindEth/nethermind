// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>A writable flat branch over sealed local snapshots and a shared read-only base.</summary>
public sealed class PbtSnapshotBundle(
    PbtSnapshotPooledList snapshots,
    PbtReadOnlySnapshotBundle readOnlyBundle,
    IPbtResourcePool resourcePool,
    PbtResourcePool.Usage usage) : IDisposable
{
    private PbtSnapshotContent? _writeBuffer = resourcePool.GetSnapshotContent(usage);
    private PbtWriteBatchBuilder? _writeBatchBuilder = resourcePool.GetWriteBatchBuilder(usage);
    private readonly Dictionary<ValueHash256, Account?> _changedAccounts = [];
    private PbtTransientResource _transientResource = resourcePool.GetCachedResource(usage);
    private bool _isDisposed;

    public ValueHash256 TreeRoot => snapshots.Count > 0 ? snapshots[^1].TreeRoot : readOnlyBundle.TreeRoot;

    internal Func<ValueHash256, byte[]?>? ReadCode { private get; set; }

    private PbtSnapshotContent WriteBuffer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return _writeBuffer!;
        }
    }

    private PbtWriteBatchBuilder WriteBatchBuilder
    {
        get
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return _writeBatchBuilder!;
        }
    }

    internal void SetLeaf(PbtFullKey key, ValueHash256? value) => WriteBatchBuilder.SetLeaf(key, value);

    internal PbtWriteBatchSet PrepareLeafChanges()
    {
        Dictionary<ValueHash256, long> referenceChanges = [];
        foreach ((ValueHash256 addressHash, Account? previous) in _changedAccounts)
        {
            Account? account = WriteBuffer.Accounts[addressHash];
            if (previous?.CodeHash == account?.CodeHash) continue;
            AddReferenceChange(previous, -1);
            AddReferenceChange(account, 1);
        }
        Dictionary<ValueHash256, ulong> referenceCounts = [];
        foreach ((ValueHash256 hash, long delta) in referenceChanges)
        {
            ulong count = checked((ulong)(checked((long)GetCodeReference(hash)) + delta));
            referenceCounts[hash] = count;
        }
        foreach ((ValueHash256 addressHash, Account? previous) in _changedAccounts)
        {
            if (previous is not null)
            {
                foreach ((PbtFullKey key, _) in PbtFlatState.AccountLeaves(addressHash, previous, previous.HasCode ? GetCode(previous.CodeHash.ValueHash256) : null))
                    if (key.Bytes[0] != Eip8297KeyDerivation.CodeZone || (referenceCounts.TryGetValue(previous.CodeHash.ValueHash256, out ulong count) ? count : GetCodeReference(previous.CodeHash.ValueHash256)) == 0) SetLeaf(key, null);
            }
            if (WriteBuffer.Accounts[addressHash] is { } account)
                foreach ((PbtFullKey key, ValueHash256 value) in PbtFlatState.AccountLeaves(addressHash, account, account.HasCode ? GetCode(account.CodeHash.ValueHash256) : null))
                    SetLeaf(key, value);
        }
        foreach ((ValueHash256 hash, ulong count) in referenceCounts) WriteBuffer.SetCodeReference(hash, count == 0 ? null : count);
        _changedAccounts.Clear();
        return WriteBatchBuilder.PrepareDrain();

        void AddReferenceChange(Account? account, long delta)
        {
            if (account is not { HasCode: true }) return;
            ValueHash256 hash = account.CodeHash.ValueHash256;
            referenceChanges.TryGetValue(hash, out long current);
            referenceChanges[hash] = current + delta;
        }
    }

    internal void CompleteLeafChanges() => WriteBatchBuilder.CompleteDrain();

    internal void SetNodeGroup(PbtNodePath groupKey, RefCountingMemory? payload) => WriteBuffer.SetNodeGroup(groupKey, payload);

    internal RefCountingMemory? GetNodeGroup(PbtNodePath groupKey)
    {
        ArgumentNullException.ThrowIfNull(groupKey);
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
            throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));
        if (WriteBuffer.TryGetNodeGroup(groupKey, out RefCountingMemory? payload)) return payload;
        for (int index = snapshots.Count - 1; index >= 0; index--)
            if (snapshots[index].Content.TryGetNodeGroup(groupKey, out payload)) return payload;
        return readOnlyBundle.GetNodeGroup(groupKey);
    }

    internal ulong GetCodeReference(in ValueHash256 codeHash)
    {
        if (WriteBuffer.TryGetCodeReference(codeHash, out ulong? count)) return count ?? 0;
        for (int index = snapshots.Count - 1; index >= 0; index--)
            if (snapshots[index].Content.TryGetCodeReference(codeHash, out count)) return count ?? 0;
        return readOnlyBundle.GetCodeReference(codeHash);
    }

    internal IEnumerable<KeyValuePair<PbtFullKey, ValueHash256?>> PendingLeafMutations() => WriteBatchBuilder.Leaves;

    internal IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves() =>
        PbtFlatState.EnumerateLeaves(EnumerateAccounts(), EnumerateStorage(), hash => GetCode(hash));

    internal IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves(PbtFullKey prefix)
    {
        foreach (KeyValuePair<PbtFullKey, ValueHash256> leaf in EnumerateLeaves())
            if (prefix.IsPrefixOf(leaf.Key)) yield return leaf;
    }

    private IEnumerable<KeyValuePair<ValueHash256, Account>> EnumerateAccounts()
    {
        Dictionary<ValueHash256, Account?> visible = [];
        foreach ((ValueHash256 hash, Account account) in readOnlyBundle.EnumerateAccounts()) visible[hash] = account;
        foreach (PbtSnapshot snapshot in snapshots)
            foreach ((ValueHash256 hash, Account? account) in snapshot.Content.Accounts) visible[hash] = account;
        foreach ((ValueHash256 hash, Account? account) in WriteBuffer.Accounts) visible[hash] = account;
        foreach ((ValueHash256 hash, Account? account) in visible)
            if (account is not null) yield return new(hash, account);
    }

    private IEnumerable<KeyValuePair<PbtFullKey, EvmWord>> EnumerateStorage(ValueHash256? addressFilter = null)
    {
        SortedDictionary<PbtFullKey, EvmWord> visible = [];
        foreach ((PbtFullKey key, EvmWord value) in readOnlyBundle.EnumerateStorage(addressFilter)) visible[key] = value;
        foreach (PbtSnapshot snapshot in snapshots) PbtFlatState.ApplyStorage(visible, snapshot.Content, addressFilter);
        PbtFlatState.ApplyStorage(visible, WriteBuffer, addressFilter);
        foreach ((PbtFullKey key, EvmWord value) in visible)
            if (!EvmWordSlot.IsZero(value)) yield return new(key, value);
    }

    internal bool HasStorage(Address address)
    {
        ValueHash256 hash = PbtKeyDerivation.AddressKeyHash(address);
        foreach (KeyValuePair<PbtFullKey, EvmWord> _ in EnumerateStorage(hash)) return true;
        return false;
    }

    public Account? GetAccount(Address address) => GetAccount(PbtKeyDerivation.AddressKeyHash(address));

    private Account? GetAccount(in ValueHash256 addressHash)
    {
        if (WriteBuffer.Accounts.TryGetValue(addressHash, out Account? account)) return account;
        for (int index = snapshots.Count - 1; index >= 0; index--)
            if (snapshots[index].Content.Accounts.TryGetValue(addressHash, out account)) return account;
        return readOnlyBundle.GetAccount(addressHash);
    }

    public EvmWord GetSlot(Address address, in UInt256 slot)
    {
        PbtFullKey key = PbtStateKey.Storage(address, slot);
        ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(address);
        if (WriteBuffer.Storages.TryGetValue(key, out EvmWord value)) return value;
        if (WriteBuffer.SelfDestructedStorageAddresses.ContainsKey(addressHash)) return default;
        for (int index = snapshots.Count - 1; index >= 0; index--)
        {
            PbtSnapshotContent content = snapshots[index].Content;
            if (content.Storages.TryGetValue(key, out value)) return value;
            if (content.SelfDestructedStorageAddresses.ContainsKey(addressHash)) return default;
        }
        return readOnlyBundle.GetSlot(key);
    }

    public void SetAccount(Address address, Account? account)
    {
        ValueHash256 hash = PbtKeyDerivation.AddressKeyHash(address);
        _changedAccounts.TryAdd(hash, GetAccount(hash));
        WriteBuffer.Accounts[hash] = account;
        if (account is null) SelfDestruct(address);
    }

    public void SetSlot(Address address, in UInt256 slot, in EvmWord value)
    {
        PbtFullKey key = PbtStateKey.Storage(address, slot);
        SetLeaf(key, EvmWordSlot.IsZero(value) ? null : new ValueHash256(EvmWordSlot.AsReadOnlySpan(in value)));
        WriteBuffer.Storages[key] = value;
    }

    public void SelfDestruct(Address address)
    {
        ValueHash256 hash = PbtKeyDerivation.AddressKeyHash(address);
        foreach ((PbtFullKey key, _) in EnumerateStorage(hash)) SetLeaf(key, null);
        WriteBuffer.ClearStorage(hash);
    }

    internal void SetCode(in ValueHash256 codeHash, CodeInfo code) => WriteBuffer.Codes[codeHash] = code;

    internal CodeInfo? GetCode(in ValueHash256 codeHash)
    {
        if (codeHash == ValueKeccak.OfAnEmptyString) return CodeInfo.Empty;
        if (WriteBuffer.Codes.TryGetValue(codeHash, out CodeInfo? code)) return code;
        for (int index = snapshots.Count - 1; index >= 0; index--)
            if (snapshots[index].Content.Codes.TryGetValue(codeHash, out code)) return code;
        code = readOnlyBundle.GetCode(codeHash);
        if (code is null && ReadCode?.Invoke(codeHash) is { } bytes)
        {
            code = new CodeInfo(bytes) { CodeHash = codeHash };
            SetCode(codeHash, code);
        }
        return code;
    }

    /// <summary>Records a prewarm hint, returning false for probable duplicates or a disposed bundle.</summary>
    public bool ShouldQueuePrewarm(Address address, UInt256? slot = null)
    {
        PbtTransientResource? transientResource = TryLeaseTransientResource();
        if (transientResource is null) return false;
        try
        {
            return transientResource.ShouldPrewarm(address, slot);
        }
        finally
        {
            transientResource.ReleaseLease();
        }
    }

    /// <inheritdoc cref="ShouldQueuePrewarm(Address, UInt256?)"/>
    public bool ShouldQueuePrewarm(in ValueAddress address, UInt256? slot = null)
    {
        PbtTransientResource? transientResource = TryLeaseTransientResource();
        if (transientResource is null) return false;
        try
        {
            return transientResource.ShouldPrewarm(address, slot);
        }
        finally
        {
            transientResource.ReleaseLease();
        }
    }

    private PbtTransientResource? TryLeaseTransientResource()
    {
        SpinWait spinWait = default;
        while (true)
        {
            if (Volatile.Read(ref _isDisposed)) return null;
            PbtTransientResource transientResource = Volatile.Read(ref _transientResource);
            if (transientResource.TryAcquireLease())
            {
                // A stale resource may already belong to another bundle after retirement and re-rental.
                if (ReferenceEquals(Volatile.Read(ref _transientResource), transientResource)
                    && !Volatile.Read(ref _isDisposed))
                    return transientResource;
                transientResource.ReleaseLease();
            }
            spinWait.SpinOnce();
        }
    }

    public PbtSnapshot CollectSnapshot(in StateId from, in StateId to, in ValueHash256 treeRoot)
    {
        if (_changedAccounts.Count != 0) throw new InvalidOperationException("Pending account changes must be folded before collecting a snapshot.");
        foreach (KeyValuePair<PbtFullKey, ValueHash256?> _ in WriteBatchBuilder.Leaves)
            throw new InvalidOperationException("Pending leaf changes must be folded before collecting a snapshot.");
        PbtSnapshot snapshot = new(from, to, treeRoot, WriteBuffer, resourcePool, usage);
        snapshot.TryLease();
        snapshots.Add(snapshot);
        _writeBuffer = resourcePool.GetSnapshotContent(usage);
        PbtTransientResource retired = _transientResource;
        Volatile.Write(ref _transientResource, resourcePool.GetCachedResource(usage));
        retired.ReleaseLease();
        return snapshot;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, true)) return;
        _changedAccounts.Clear();
        ReadCode = null;
        PbtSnapshotContent? buffer = _writeBuffer;
        _writeBuffer = null;
        PbtWriteBatchBuilder? builder = _writeBatchBuilder;
        _writeBatchBuilder = null;
        try
        {
            snapshots.Dispose();
            if (buffer is not null) resourcePool.ReturnSnapshotContent(usage, buffer);
        }
        finally
        {
            try
            {
                if (builder is not null) resourcePool.ReturnWriteBatchBuilder(usage, builder);
            }
            finally
            {
                try
                {
                    _transientResource.ReleaseLease();
                }
                finally
                {
                    readOnlyBundle.Dispose();
                }
            }
        }
    }
}
