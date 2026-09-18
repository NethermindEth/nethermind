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
using Nethermind.State.Pbt.Persistence;
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
    private readonly PbtWriteBatchBuilder<PbtPath> _accountBatch = resourcePool.GetWriteBatch(usage);
    private readonly PbtWriteBatchBuilder<PbtPath> _codeBatch = resourcePool.GetWriteBatch(usage);
    private readonly PbtWriteBatchBuilder<PbtStoragePath> _storageBatch = resourcePool.GetStorageWriteBatch(usage);
    private readonly Lock _accountLock = new();
    private readonly Dictionary<ValueHash256, ValueHash256> _accountsAwaitingCode = [];
    // Read-through memo of bytecode served by the read-only base; never snapshot content, so it is not persisted.
    private readonly ConcurrentDictionary<ValueHash256, CodeInfo> _codeMemo = new();
    private PbtTransientResource _transientResource = resourcePool.GetCachedResource(usage);
    // Storage commits may write one run from several threads; a stripe serializes the read-modify-replace of a run.
    private const int RunLockStripes = 64;
    private readonly Lock[] _runLocks = CreateRunLocks();
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

    private static Lock[] CreateRunLocks()
    {
        Lock[] locks = new Lock[RunLockStripes];
        for (int stripe = 0; stripe < locks.Length; stripe++) locks[stripe] = new Lock();
        return locks;
    }

    private void SetPbtLeaf(PbtPath key, ValueHash256? value)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        int partition = PbtWriteBatchSet<PbtPath>.PartitionOf(key);
        if (partition == (int)PbtPartition.Account) _accountBatch.SetLeaf(key, value);
        else if (partition == (int)PbtPartition.Code) _codeBatch.SetLeaf(key, value);
        else throw new ArgumentException("A canonical account or code key is required.", nameof(key));
    }

    private void SetPbtLeaf(in PbtStorageTreeKey key, ValueHash256? value)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (PbtWriteBatchSet<PbtStorageTreeKey>.PartitionOf(key) == (int)PbtPartition.Storage) _storageBatch.SetLeaf((PbtStoragePath)key, value);
        else SetPbtLeaf((PbtPath)key, value);
    }

    internal PbtPartitionBatches PrepareLeafChanges()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        lock (_accountLock)
        {
            foreach ((ValueHash256 addressHash, ValueHash256 awaitedCodeHash) in _accountsAwaitingCode)
            {
                CodeInfo code = GetCode(awaitedCodeHash) ?? throw new InvalidDataException($"Missing PBT bytecode for {awaitedCodeHash}.");
                WriteAccountLeaves(addressHash, WriteBuffer.Accounts[addressHash]!, code);
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

    internal IEnumerable<KeyValuePair<PbtStorageTreeKey, EvmWord>> EnumerateStorage(ValueHash256? addressFilter = null)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        SortedDictionary<PbtStorageTreeKey, EvmWord> changes = new(PbtStorageKeyLayout.Comparer);
        HashSet<ValueHash256> clearedAddresses = [];
        foreach (PbtSnapshot snapshot in snapshots) ApplyChanges(snapshot.Content);
        ApplyChanges(WriteBuffer);
        using IEnumerator<KeyValuePair<PbtStorageTreeKey, EvmWord>> changed = changes.GetEnumerator();
        bool hasChange = changed.MoveNext();
        foreach (KeyValuePair<PbtStorageTreeKey, EvmWord> persisted in readOnlyBundle.EnumerateStorage(addressFilter))
        {
            while (hasChange && PbtStorageKeyLayout.Comparer.Compare(changed.Current.Key, persisted.Key) < 0)
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

    public EvmWord GetSlot(Address address, in UInt256 slot) => GetSlot(address, PbtKeyDerivation.AddressKeyHash(address), slot);

    /// <inheritdoc cref="GetSlot(Address, in UInt256)"/>
    public EvmWord GetSlot(Address address, in ValueHash256 addressHash, in UInt256 slot)
    {
        HashedKey<PbtStorageTreeKey> runKey = PbtStateKey.StorageRun(address, addressHash, slot, out int index);
        return BufferRun(runKey, addressHash).Get(index);
    }

    /// <summary>The run as the write buffer holds it, borrowed; the first touch of a run buffers it as currently visible.</summary>
    private ISlotRun BufferRun(in HashedKey<PbtStorageTreeKey> runKey, in ValueHash256 addressHash)
    {
        if (WriteBuffer.TryGetSlotRun(runKey, out ISlotRun? run)) return run;
        lock (_runLocks[(uint)runKey.GetHashCode() % RunLockStripes])
        {
            if (WriteBuffer.TryGetSlotRun(runKey, out run)) return run;
            run = FindLocalRun(runKey, addressHash)?.Clone() ?? readOnlyBundle.RentRun(runKey, addressHash);
            WriteBuffer.SetRun(runKey, run);
            return run;
        }
    }

    public void SetAccount(Address address, Account? account)
    {
        lock (_accountLock)
        {
            ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(address);
            Account? previous = GetAccount(addressHash);
            // Code chunk leaves are shared per code hash without a reference count, so a non-delegation code hash
            // can never be replaced or removed once set (EIP-6780 and EIP-161 guarantee this in protocol execution).
            if (previous is { HasCode: true } && previous.CodeHash != account?.CodeHash)
            {
                CodeInfo previousCode = GetCode(previous.CodeHash.ValueHash256) ?? throw new InvalidDataException($"Missing PBT bytecode for {previous.CodeHash}.");
                if (!Eip7702Constants.IsDelegatedCode(previousCode.CodeSpan))
                    throw new InvalidOperationException($"The code of {address} cannot be replaced or removed: EIP-8297 code leaves are shared by code hash.");
            }
            CodeInfo? code = account is { HasCode: true } ? GetCode(account.CodeHash.ValueHash256) : null;

            WriteAccountLeaves(addressHash, account, code);

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

    private void WriteAccountLeaves(ValueHash256 addressHash, Account? account, CodeInfo? code)
    {
        bool isDelegation = code is not null && Eip7702Constants.IsDelegatedCode(code.CodeSpan);
        ValueHash256 basicData = default;
        if (account is not null && (!account.HasCode || code is not null))
        {
            PbtKeyDerivation.PackBasicData(basicData.BytesAsSpan, (uint)(code?.Code.Length ?? 0), account.Nonce, account.Balance);
            if (isDelegation)
            {
                ValueHash256 delegation = default;
                code!.CodeSpan.CopyTo(delegation.BytesAsSpan);
                SetPbtLeaf(PbtStateKey.Account(addressHash, PbtKeyDerivation.DelegationLeafKey), delegation);
            }
            else
            {
                SetPbtLeaf(PbtStateKey.Account(addressHash, PbtKeyDerivation.CodeHashLeafKey), account.CodeHash.ValueHash256);
                if (code is not null)
                {
                    int chunkCount = (code.Code.Length + 30) / 31;
                    using ArrayPoolListRef<byte> chunks = new(chunkCount * PbtKeyDerivation.CodeChunkSize, chunkCount * PbtKeyDerivation.CodeChunkSize);
                    PbtKeyDerivation.ChunkifyCode(code.CodeSpan, chunks.AsSpan());
                    for (int chunkId = 0; chunkId < chunkCount; chunkId++)
                    {
                        ValueHash256 chunk = new(chunks.AsSpan().Slice(chunkId * PbtKeyDerivation.CodeChunkSize, PbtKeyDerivation.CodeChunkSize));
                        if (chunk != default) SetPbtLeaf(PbtStateKey.Code(addressHash, account.CodeHash.ValueHash256, chunkId), chunk);
                    }
                }
            }
        }
        else
        {
            SetPbtLeaf(PbtStateKey.Account(addressHash, PbtKeyDerivation.CodeHashLeafKey), account?.CodeHash.ValueHash256);
        }
        // A zero basic-data value is stored as a deletion by the batch builder.
        SetPbtLeaf(PbtStateKey.Account(addressHash, PbtKeyDerivation.BasicDataLeafKey), basicData);
        SetPbtLeaf(PbtStateKey.Account(addressHash, isDelegation ? (byte)PbtKeyDerivation.CodeHashLeafKey : (byte)PbtKeyDerivation.DelegationLeafKey), null);
    }

    public void SetSlot(Address address, in UInt256 slot, in EvmWord value)
    {
        PbtStorageTreeKey key = PbtStateKey.Storage(address, slot);
        SetPbtLeaf(key, EvmWordSlot.IsZero(value) ? null : new ValueHash256(EvmWordSlot.AsReadOnlySpan(in value)));
        HashedKey<PbtStorageTreeKey> runKey = SlotRun.RunKey(key);
        ValueHash256 addressHash = PbtFlatState.StorageAddress(key);
        lock (_runLocks[(uint)runKey.GetHashCode() % RunLockStripes])
        {
            WriteBuffer.SetRun(runKey, BufferRun(runKey, addressHash).With(SlotRun.IndexOf(key), value));
        }
    }

    /// <summary>The run as the local snapshots see it, borrowed; null when they say nothing about it.</summary>
    private ISlotRun? FindLocalRun(in HashedKey<PbtStorageTreeKey> runKey, in ValueHash256 addressHash)
    {
        if (WriteBuffer.SelfDestructedStorageAddresses.ContainsKey(addressHash)) return SlotRun.Empty;
        for (int layer = snapshots.Count - 1; layer >= 0; layer--)
        {
            PbtSnapshotContent content = snapshots[layer].Content;
            if (content.TryGetSlotRun(runKey, out ISlotRun? run)) return run;
            if (content.SelfDestructedStorageAddresses.ContainsKey(addressHash)) return SlotRun.Empty;
        }
        return null;
    }

    public void SelfDestruct(Address address)
    {
        ValueHash256 hash = PbtKeyDerivation.AddressKeyHash(address);
        foreach ((PbtStorageTreeKey key, _) in EnumerateStorage(hash)) SetPbtLeaf(key, null);
        WriteBuffer.ClearStorage(hash);
    }

    internal void SetCode(in ValueHash256 codeHash, CodeInfo code)
    {
        lock (_accountLock)
        {
            WriteBuffer.Codes[codeHash] = code;
            using ArrayPoolListRef<ValueHash256> resolved = new(0);
            foreach ((ValueHash256 addressHash, ValueHash256 awaitedCodeHash) in _accountsAwaitingCode)
            {
                if (awaitedCodeHash != codeHash) continue;
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
        if (_codeMemo.TryGetValue(codeHash, out code)) return code;
        code = readOnlyBundle.GetCode(codeHash);
        if (code is not null) _codeMemo[codeHash] = code;
        else if (ReadCode?.Invoke(codeHash) is { } bytes)
        {
            code = new CodeInfo(bytes);
            WriteBuffer.Codes[codeHash] = code;
        }
        return code;
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
