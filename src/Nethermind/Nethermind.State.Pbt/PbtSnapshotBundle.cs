// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Pbt.ScopeProvider;

namespace Nethermind.State.Pbt;

/// <summary>A writable flat branch over sealed local snapshots and a shared read-only base.</summary>
public sealed class PbtSnapshotBundle(
    PbtSnapshotPooledList snapshots,
    PbtReadOnlySnapshotBundle readOnlyBundle,
    IPbtResourcePool resourcePool,
    PbtResourcePool.Usage usage,
    PbtTrieNodeCache? trieNodeCache = null) : IDisposable
{
    private PbtSnapshotContent? _writeBuffer = resourcePool.GetSnapshotContent(usage);
    private readonly PbtWriteBatchBuilder<PbtFullKey> _accountBatch = resourcePool.GetWriteBatch(usage);
    private readonly PbtWriteBatchBuilder<PbtFullKey> _codeBatch = resourcePool.GetWriteBatch(usage);
    private readonly PbtWriteBatchBuilder<PbtStorageFullKey> _storageBatch = resourcePool.GetStorageWriteBatch(usage);
    private readonly Lock _accountLock = new();
    private readonly Dictionary<ValueHash256, AwaitedCode> _accountsAwaitingCode = [];
    // Read-through memo of bytecode served by the read-only base; never snapshot content, so it is not persisted.
    private readonly ConcurrentDictionary<ValueHash256, CodeInfo> _codeMemo = new();
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

    internal int PendingMutationCount => _accountBatch.Count + _codeBatch.Count + _storageBatch.Count;

    private void SetPbtLeaf(PbtFullKey key, ValueHash256? value)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        int partition = PbtWriteBatchSet<PbtFullKey>.PartitionOf(key);
        if (partition == (int)PbtPartition.Account) _accountBatch.SetLeaf(key, value);
        else if (partition == (int)PbtPartition.Code) _codeBatch.SetLeaf(key, value);
        else throw new ArgumentException("A canonical account or code key is required.", nameof(key));
    }

    private void SetPbtLeaf(PbtStorageFullKey key, ValueHash256? value)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (PbtWriteBatchSet<PbtStorageFullKey>.PartitionOf(key) == (int)PbtPartition.Storage) _storageBatch.SetLeaf(key, value);
        else SetPbtLeaf((PbtFullKey)key, value);
    }

    internal PbtPartitionBatches PrepareLeafChanges()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        lock (_accountLock)
        {
            foreach ((ValueHash256 addressHash, AwaitedCode awaited) in _accountsAwaitingCode)
            {
                CodeInfo code = GetCode(awaited.CodeHash) ?? throw new InvalidDataException($"Missing PBT bytecode for {awaited.CodeHash}.");
                WriteAccountLeaves(addressHash, WriteBuffer.Accounts[addressHash]!, code, awaited.IncludeCode);
            }
            _accountsAwaitingCode.Clear();
        }
        PbtPartitionBatches changes = new();
        try
        {
            if (_accountBatch.Count != 0) changes.Account = _accountBatch.Build();
            if (_codeBatch.Count != 0) changes.Code = _codeBatch.Build();
            if (_storageBatch.Count != 0) changes.Storage = _storageBatch.Build();
            return changes;
        }
        catch
        {
            changes.Dispose();
            throw;
        }
    }

    internal void CompleteLeafChanges()
    {
        _accountBatch.CompleteDrain();
        _codeBatch.CompleteDrain();
        _storageBatch.CompleteDrain();
    }

    internal void SetNodeGroup<TPath>(TPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath> => WriteBuffer.SetNodeGroup(groupKey, payload);

    internal RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey, in ValueHash256 groupHash) where TPath : struct, IPbtNodePath<TPath>
    {
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
            throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));
        PbtStorageNodePath storagePath = groupKey.ToPath<PbtStorageNodePath>();
        if (WriteBuffer.TryGetNodeGroup(storagePath, out RefCountingMemory? payload)) return payload;
        for (int index = snapshots.Count - 1; index >= 0; index--)
            if (snapshots[index].Content.TryGetNodeGroup(storagePath, out payload)) return payload;
        if (trieNodeCache?.TryGet(groupHash, storagePath, out payload) == true) return payload;
        payload = readOnlyBundle.GetNodeGroup(storagePath);
        if (payload is not null) trieNodeCache?.Add(groupHash, storagePath, payload);
        return payload;
    }

    internal ulong GetCodeReference(in ValueHash256 codeHash)
    {
        if (WriteBuffer.TryGetCodeReference(codeHash, out ulong? count)) return count ?? 0;
        for (int index = snapshots.Count - 1; index >= 0; index--)
            if (snapshots[index].Content.TryGetCodeReference(codeHash, out count)) return count ?? 0;
        return readOnlyBundle.GetCodeReference(codeHash);
    }

    internal IEnumerable<KeyValuePair<PbtStorageFullKey, ValueHash256?>> EnumeratePendingLeafMutationsForTest()
    {
        foreach ((PbtFullKey key, ValueHash256? value) in _accountBatch.Leaves) yield return new((PbtStorageFullKey)key, value);
        foreach ((PbtFullKey key, ValueHash256? value) in _codeBatch.Leaves) yield return new((PbtStorageFullKey)key, value);
        foreach (KeyValuePair<PbtStorageFullKey, ValueHash256?> mutation in _storageBatch.Leaves) yield return mutation;
    }

    internal IEnumerable<KeyValuePair<PbtStorageFullKey, ValueHash256>> EnumerateLeaves() =>
        PbtFlatState.EnumerateLeaves(EnumerateAccounts(), EnumerateStorage(), hash => GetCode(hash));

    internal IEnumerable<KeyValuePair<PbtStorageFullKey, ValueHash256>> EnumerateLeaves(PbtStorageFullKey prefix)
    {
        foreach (KeyValuePair<PbtStorageFullKey, ValueHash256> leaf in EnumerateLeaves())
            if (prefix.IsPrefixOf(leaf.Key)) yield return leaf;
    }

    internal IEnumerable<KeyValuePair<ValueHash256, Account>> EnumerateAccounts()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        SortedDictionary<ValueHash256, Account?> changes = new(Comparer<ValueHash256>.Create(static (left, right) => left.Bytes.SequenceCompareTo(right.Bytes)));
        foreach (PbtSnapshot snapshot in snapshots)
            foreach ((ValueHash256 hash, Account? account) in snapshot.Content.Accounts) changes[hash] = account;
        foreach ((ValueHash256 hash, Account? account) in WriteBuffer.Accounts) changes[hash] = account;
        using IEnumerator<KeyValuePair<ValueHash256, Account?>> changed = changes.GetEnumerator();
        bool hasChange = changed.MoveNext();
        foreach (KeyValuePair<ValueHash256, Account> persisted in readOnlyBundle.EnumerateAccounts())
        {
            while (hasChange && changes.Comparer.Compare(changed.Current.Key, persisted.Key) < 0)
            {
                if (changed.Current.Value is { } account) yield return new(changed.Current.Key, account);
                hasChange = changed.MoveNext();
            }
            if (hasChange && changed.Current.Key == persisted.Key)
            {
                if (changed.Current.Value is { } account) yield return new(changed.Current.Key, account);
                hasChange = changed.MoveNext();
            }
            else yield return persisted;
        }
        while (hasChange)
        {
            if (changed.Current.Value is { } account) yield return new(changed.Current.Key, account);
            hasChange = changed.MoveNext();
        }
    }

    internal IEnumerable<KeyValuePair<PbtStorageFullKey, EvmWord>> EnumerateStorage(ValueHash256? addressFilter = null)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        SortedDictionary<PbtStorageFullKey, EvmWord> changes = [];
        HashSet<ValueHash256> clearedAddresses = [];
        foreach (PbtSnapshot snapshot in snapshots) ApplyChanges(snapshot.Content);
        ApplyChanges(WriteBuffer);
        using IEnumerator<KeyValuePair<PbtStorageFullKey, EvmWord>> changed = changes.GetEnumerator();
        bool hasChange = changed.MoveNext();
        foreach (KeyValuePair<PbtStorageFullKey, EvmWord> persisted in readOnlyBundle.EnumerateStorage(addressFilter))
        {
            while (hasChange && changed.Current.Key.CompareTo(persisted.Key) < 0)
            {
                if (!EvmWordSlot.IsZero(changed.Current.Value)) yield return changed.Current;
                hasChange = changed.MoveNext();
            }
            if (hasChange && changed.Current.Key == persisted.Key)
            {
                if (!EvmWordSlot.IsZero(changed.Current.Value)) yield return changed.Current;
                hasChange = changed.MoveNext();
            }
            else if (!clearedAddresses.Contains(PbtFlatState.StorageAddress(persisted.Key))) yield return persisted;
        }
        while (hasChange)
        {
            if (!EvmWordSlot.IsZero(changed.Current.Value)) yield return changed.Current;
            hasChange = changed.MoveNext();
        }

        void ApplyChanges(PbtSnapshotContent content)
        {
            PbtFlatState.ApplyStorage(changes, content, addressFilter);
            foreach ((ValueHash256 addressHash, _) in content.SelfDestructedStorageAddresses)
                if (addressFilter is null || addressHash == addressFilter.Value) clearedAddresses.Add(addressHash);
        }
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
        PbtStorageFullKey key = PbtStateKey.Storage(address, slot);
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

            // Code chunk leaves are shared per code hash, so they are written only when this update makes the
            // code referenced; an unresolved earlier decision for the same code carries over unchanged.
            bool includeCode = _accountsAwaitingCode.TryGetValue(addressHash, out AwaitedCode awaited) && awaited.IncludeCode;
            if (previous?.CodeHash != account?.CodeHash)
            {
                includeCode = false;
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
                    ulong count = GetCodeReference(codeHash);
                    includeCode = count == 0;
                    WriteBuffer.SetCodeReference(codeHash, checked(count + 1));
                }
            }

            WriteAccountLeaves(addressHash, account, code, includeCode);

            _accountsAwaitingCode.Remove(addressHash);
            WriteBuffer.Accounts[addressHash] = account;
            if (account is null)
            {
                SelfDestruct(address);
            }
            else if (account.HasCode && code is null)
            {
                _accountsAwaitingCode[addressHash] = new AwaitedCode(account.CodeHash.ValueHash256, includeCode);
            }
        }
    }

    private void WriteAccountLeaves(ValueHash256 addressHash, Account? account, CodeInfo? code, bool includeCode)
    {
        bool hasBasicData = false;
        if (account is not null && (!account.HasCode || code is not null))
        {
            foreach ((PbtFullKey key, ValueHash256 value) in PbtFlatState.AccountLeaves(addressHash, account, code, includeCode))
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
        byte inactiveCodeLeaf = code is not null && Eip7702Constants.IsDelegatedCode(code.CodeSpan)
            ? (byte)PbtKeyDerivation.CodeHashLeafKey
            : (byte)PbtKeyDerivation.DelegationLeafKey;
        SetPbtLeaf(PbtStateKey.Account(addressHash, inactiveCodeLeaf), null);
    }

    public void SetSlot(Address address, in UInt256 slot, in EvmWord value)
    {
        PbtStorageFullKey key = PbtStateKey.Storage(address, slot);
        SetPbtLeaf(key, EvmWordSlot.IsZero(value) ? null : new ValueHash256(EvmWordSlot.AsReadOnlySpan(in value)));
        WriteBuffer.Storages[key] = value;
    }

    public void SelfDestruct(Address address)
    {
        ValueHash256 hash = PbtKeyDerivation.AddressKeyHash(address);
        foreach ((PbtStorageFullKey key, _) in EnumerateStorage(hash)) SetPbtLeaf(key, null);
        WriteBuffer.ClearStorage(hash);
    }

    internal void SetCode(in ValueHash256 codeHash, CodeInfo code)
    {
        lock (_accountLock)
        {
            WriteBuffer.Codes[codeHash] = code;
            using ArrayPoolListRef<ValueHash256> resolved = new(0);
            foreach ((ValueHash256 addressHash, AwaitedCode awaited) in _accountsAwaitingCode)
            {
                if (awaited.CodeHash != codeHash) continue;
                WriteAccountLeaves(addressHash, WriteBuffer.Accounts[addressHash]!, code, awaited.IncludeCode);
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
        if (_codeMemo.TryGetValue(codeHash, out code)) return code;
        code = readOnlyBundle.GetCode(codeHash);
        if (code is not null) _codeMemo[codeHash] = code;
        else if (ReadCode?.Invoke(codeHash) is { } bytes)
        {
            code = new CodeInfo(bytes) { CodeHash = codeHash };
            WriteBuffer.Codes[codeHash] = code;
        }
        return code;
    }

    private readonly record struct AwaitedCode(ValueHash256 CodeHash, bool IncludeCode);

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

    // The owning scope serializes capture with snapshot collection and disposal.
    internal PbtTrieWarmupSession CreateTrieWarmupSession(ITrieWarmer trieWarmer, int sequenceId)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        PbtSnapshotPooledList initialSnapshots = new(snapshots.Count);
        bool readOnlyLeased = false;
        bool transientLeased = false;
        try
        {
            foreach (PbtSnapshot snapshot in snapshots)
            {
                if (!snapshot.TryLease()) throw new ObjectDisposedException(nameof(PbtSnapshot));
                try
                {
                    initialSnapshots.Add(snapshot);
                }
                catch
                {
                    snapshot.Dispose();
                    throw;
                }
            }
            readOnlyLeased = readOnlyBundle.TryLease();
            if (!readOnlyLeased) throw new ObjectDisposedException(nameof(PbtReadOnlySnapshotBundle));
            transientLeased = _transientResource.TryAcquireLease();
            if (!transientLeased) throw new ObjectDisposedException(nameof(PbtTransientResource));
            return new PbtTrieWarmupSession(initialSnapshots, readOnlyBundle, _transientResource, trieWarmer, sequenceId, trieNodeCache);
        }
        catch
        {
            try
            {
                if (transientLeased) _transientResource.ReleaseLease();
            }
            finally
            {
                try
                {
                    if (readOnlyLeased) readOnlyBundle.Dispose();
                }
                finally
                {
                    initialSnapshots.Dispose();
                }
            }
            throw;
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
        _codeMemo.Clear();
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
                    resourcePool.ReturnWriteBatch(usage, _accountBatch);
                }
                finally
                {
                    try
                    {
                        resourcePool.ReturnWriteBatch(usage, _codeBatch);
                    }
                    finally
                    {
                        resourcePool.ReturnStorageWriteBatch(usage, _storageBatch);
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
