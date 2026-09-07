// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
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
    private readonly Dictionary<PbtPartition, PbtWriteBatchBuilder> _writeBatches = new()
    {
        [PbtPartition.Account] = resourcePool.GetWriteBatch(usage),
        [PbtPartition.Code] = resourcePool.GetWriteBatch(usage),
        [PbtPartition.Storage] = resourcePool.GetWriteBatch(usage),
    };
    private readonly Lock _accountLock = new();
    private readonly Dictionary<ValueHash256, ValueHash256> _accountsAwaitingCode = [];
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

    internal int PendingMutationCount
    {
        get
        {
            int count = 0;
            foreach (PbtWriteBatchBuilder batch in _writeBatches.Values) count += batch.Count;
            return count;
        }
    }

    private void SetPbtLeaf(PbtFullKey key, ValueHash256? value)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        int partition = PbtWriteBatchSet.PartitionOf(key);
        if (partition < 0) throw new ArgumentException("A canonical account, code or storage key is required.", nameof(key));
        _writeBatches[(PbtPartition)partition].SetLeaf(key, value);
    }

    internal IReadOnlyDictionary<PbtPartition, PbtWriteBatch> PrepareLeafChanges()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        lock (_accountLock)
        {
            foreach ((ValueHash256 addressHash, ValueHash256 codeHash) in _accountsAwaitingCode)
            {
                CodeInfo code = GetCode(codeHash) ?? throw new InvalidDataException($"Missing PBT bytecode for {codeHash}.");
                WriteAccountLeaves(addressHash, WriteBuffer.Accounts[addressHash]!, code);
            }
            _accountsAwaitingCode.Clear();
        }
        Dictionary<PbtPartition, PbtWriteBatch> changes = new(_writeBatches.Count);
        try
        {
            foreach ((PbtPartition partition, PbtWriteBatchBuilder batch) in _writeBatches)
                if (batch.Count != 0) changes.Add(partition, batch.Build());
            return changes;
        }
        catch
        {
            foreach (PbtWriteBatch batch in changes.Values) batch.Dispose();
            throw;
        }
    }

    internal void CompleteLeafChanges()
    {
        foreach (PbtWriteBatchBuilder batch in _writeBatches.Values) batch.CompleteDrain();
    }

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

    internal IEnumerable<KeyValuePair<PbtFullKey, ValueHash256?>> EnumeratePendingLeafMutationsForTest()
    {
        foreach (PbtWriteBatchBuilder batch in _writeBatches.Values)
            foreach (KeyValuePair<PbtFullKey, ValueHash256?> mutation in batch.Leaves) yield return mutation;
    }

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
        lock (_accountLock)
        {
            ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(address);
            Account? previous = GetAccount(addressHash);
            CodeInfo? previousCode = previous is { HasCode: true } ? GetCode(previous.CodeHash.ValueHash256) : null;
            if (previous is { HasCode: true } && previousCode is null && !_accountsAwaitingCode.ContainsKey(addressHash))
                throw new InvalidDataException($"Missing PBT bytecode for {previous.CodeHash}.");
            CodeInfo? code = account is { HasCode: true } ? GetCode(account.CodeHash.ValueHash256) : null;

            if (previous?.CodeHash != account?.CodeHash)
            {
                if (previous is { HasCode: true })
                {
                    ValueHash256 previousHash = previous.CodeHash.ValueHash256;
                    ulong count = checked(GetCodeReference(previousHash) - 1);
                    WriteBuffer.SetCodeReference(previousHash, count == 0 ? null : count);
                    if (count == 0 && previousCode is not null)
                        foreach ((PbtFullKey key, _) in PbtFlatState.AccountLeaves(addressHash, previous, previousCode))
                            if (key.Bytes[0] == Eip8297KeyDerivation.CodeZone) SetPbtLeaf(key, null);
                }
                if (account is { HasCode: true })
                {
                    ValueHash256 codeHash = account.CodeHash.ValueHash256;
                    WriteBuffer.SetCodeReference(codeHash, checked(GetCodeReference(codeHash) + 1));
                }
            }

            WriteAccountLeaves(addressHash, account, code, previous, previousCode);

            _accountsAwaitingCode.Remove(addressHash);
            WriteBuffer.Accounts[addressHash] = account;
            if (account is null)
            {
                SelfDestruct(address);
            }
            else if (account.HasCode && code is null)
            {
                _accountsAwaitingCode[addressHash] = account.CodeHash.ValueHash256;
            }
        }
    }

    private void WriteAccountLeaves(ValueHash256 addressHash, Account? account, CodeInfo? code, Account? previous = null, CodeInfo? previousCode = null)
    {
        if (previous is not null && previousCode is not null)
            foreach ((PbtFullKey key, _) in PbtFlatState.AccountLeaves(addressHash, previous, previousCode, includeOverflowCode: false))
                if (key.Bytes[^1] >= PbtKeyDerivation.CodeOffset) SetPbtLeaf(key, null);

        bool hasBasicData = false;
        if (account is not null && (!account.HasCode || code is not null))
        {
            foreach ((PbtFullKey key, ValueHash256 value) in PbtFlatState.AccountLeaves(addressHash, account, code))
            {
                if (key.Bytes[0] == Eip8297KeyDerivation.AccountZone && key.Bytes[^1] == PbtKeyDerivation.BasicDataLeafKey)
                    hasBasicData = true;
                SetPbtLeaf(key, value);
            }
        }
        else
        {
            SetPbtLeaf(PbtStateKey.Account(addressHash, PbtKeyDerivation.CodeHashLeafKey), account?.CodeHash.ValueHash256);
        }
        if (!hasBasicData) SetPbtLeaf(PbtStateKey.Account(addressHash, PbtKeyDerivation.BasicDataLeafKey), null);
    }

    public void SetSlot(Address address, in UInt256 slot, in EvmWord value)
    {
        PbtFullKey key = PbtStateKey.Storage(address, slot);
        SetPbtLeaf(key, EvmWordSlot.IsZero(value) ? null : new ValueHash256(EvmWordSlot.AsReadOnlySpan(in value)));
        WriteBuffer.Storages[key] = value;
    }

    public void SelfDestruct(Address address)
    {
        ValueHash256 hash = PbtKeyDerivation.AddressKeyHash(address);
        foreach ((PbtFullKey key, _) in EnumerateStorage(hash)) SetPbtLeaf(key, null);
        WriteBuffer.ClearStorage(hash);
    }

    internal void SetCode(in ValueHash256 codeHash, CodeInfo code)
    {
        lock (_accountLock)
        {
            WriteBuffer.Codes[codeHash] = code;
            using ArrayPoolListRef<ValueHash256> resolved = new(0);
            foreach ((ValueHash256 addressHash, ValueHash256 pendingHash) in _accountsAwaitingCode)
            {
                if (pendingHash != codeHash) continue;
                WriteAccountLeaves(addressHash, WriteBuffer.Accounts[addressHash]!, code);
                resolved.Add(addressHash);
            }
            foreach (ValueHash256 addressHash in resolved) _accountsAwaitingCode.Remove(addressHash);
        }
    }

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
            WriteBuffer.Codes[codeHash] = code;
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
        if (_accountsAwaitingCode.Count != 0 || PendingMutationCount != 0)
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
        _accountsAwaitingCode.Clear();
        ReadCode = null;
        PbtSnapshotContent? buffer = _writeBuffer;
        _writeBuffer = null;
        try
        {
            snapshots.Dispose();
            if (buffer is not null) resourcePool.ReturnSnapshotContent(usage, buffer);
        }
        finally
        {
            try
            {
                try
                {
                    resourcePool.ReturnWriteBatch(usage, _writeBatches[PbtPartition.Account]);
                }
                finally
                {
                    try
                    {
                        resourcePool.ReturnWriteBatch(usage, _writeBatches[PbtPartition.Code]);
                    }
                    finally
                    {
                        resourcePool.ReturnWriteBatch(usage, _writeBatches[PbtPartition.Storage]);
                    }
                }
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
