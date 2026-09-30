// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Logging;
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
    IPbtTrieNodeCache trieNodeCache) : IDisposable
{
    private PbtSnapshotContent? _writeBuffer = resourcePool.GetSnapshotContent(usage);
    private readonly PbtWriteBatchBuilder<PbtPath> _accountBatch = resourcePool.GetWriteBatch(usage);
    private readonly PbtWriteBatchBuilder<PbtPath> _codeBatch = resourcePool.GetWriteBatch(usage);
    private readonly PbtWriteBatchBuilder<PbtStoragePath> _storageBatch = resourcePool.GetStorageWriteBatch(usage);
    // Accounts set before their code arrived; they reach the write buffer once the code converts them to their stem.
    private readonly ConcurrentDictionary<ValueHash256, Account> _accountsAwaitingCode = new();
    // Read-through memo of bytecode served by the read-only base; never snapshot content, so it is not persisted.
    private readonly ConcurrentDictionary<ValueHash256, CodeInfo> _codeMemo = new();
    // Accounts the layer above read past this bundle for the block in the write buffer, so a write never re-reads them.
    private readonly ConcurrentDictionary<ValueHash256, Account?> _hintedAccounts = new();
    private PbtTransientResource _transientResource = resourcePool.GetCachedResource(usage);
    // Storage commits may write one run from several threads, so a write replaces its run by compare-and-swap.
    // A replaced run is held here until the write buffer is sealed: returned earlier, it could be re-rented and
    // stored back under the same key, and a writer still comparing against it would overwrite a newer run (ABA).
    private readonly ConcurrentQueue<ISlotRun> _replacedRuns = new();
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

    internal int PendingMutationCount => _accountBatch.Count + _codeBatch.Count + _storageBatch.Count;

    private void SetPbtLeaf(PbtPath key, ValueHash256? value)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        int partition = PbtPartitions.PartitionOf(key);
        if (partition == (int)PbtPartition.Account) _accountBatch.SetLeaf(key, value);
        else if (partition == (int)PbtPartition.Code) _codeBatch.SetLeaf(key, value);
        else throw new ArgumentException("A canonical account or code key is required.", nameof(key));
    }

    private void SetPbtLeaf(in PbtStorageTreeKey key, ValueHash256? value)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (PbtPartitions.PartitionOf(key) == (int)PbtPartition.Storage) _storageBatch.SetLeaf((PbtStoragePath)key, value);
        else SetPbtLeaf((PbtPath)key, value);
    }

    internal PbtPartitionBatches PrepareLeafChanges()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        foreach ((ValueHash256 addressHash, Account awaiting) in _accountsAwaitingCode)
        {
            CodeInfo code = GetCode(awaiting.CodeHash.ValueHash256) ?? throw new InvalidDataException($"Missing PBT bytecode for {awaiting.CodeHash}.");
            StoreAccount(addressHash, PbtAccount.From(awaiting, code));
        }
        _accountsAwaitingCode.Clear();
        PbtPartitionBatches changes = new();
        try
        {
            changes.Account = _accountBatch.Build();
            changes.Code = _codeBatch.Build();
            changes.Storage = _storageBatch.Build();
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

    internal void SetNodeGroup<TPath>(TPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>
    {
        WriteBuffer.SetNodeGroup(groupKey, payload);
        _transientResource.NodeGroups.Set(groupHash, groupKey, payload);
    }

    internal RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey, in ValueHash256 groupHash) where TPath : struct, IPbtNodePath<TPath>
    {
        PbtStorageNodePath storagePath = groupKey.ToPath<PbtStorageNodePath>();
        if (WriteBuffer.TryGetNodeGroup(storagePath, out RefCountingMemory? payload)) return payload;
        for (int index = snapshots.Count - 1; index >= 0; index--)
            if (snapshots[index].Content.TryGetNodeGroup(storagePath, out payload)) return payload;
        if (_transientResource.NodeGroups.TryGet(groupHash, storagePath, out payload)) return payload;
        if (trieNodeCache.TryGet(groupHash, storagePath, out payload)) return payload;
        return readOnlyBundle.GetNodeGroup(storagePath);
    }

    public Account? GetAccount(Address address) => ReadAccount(PbtKeyDerivation.AddressKeyHash(address), promote: false);

    /// <summary>Reads an account, promoting one found past the write buffer and the hint memo into the write buffer.</summary>
    /// <remarks>The promoted account is sealed into the next snapshot, so later heads read it from the newest layer.</remarks>
    public Account? GetAndPromoteAccount(Address address) => ReadAccount(PbtKeyDerivation.AddressKeyHash(address), promote: true);

    private Account? ReadAccount(in ValueHash256 addressHash, bool promote)
    {
        if (_accountsAwaitingCode.TryGetValue(addressHash, out Account? awaiting)) return awaiting;
        if (WriteBuffer.Accounts.TryGetValue(addressHash, out PbtAccount? buffered)) return buffered?.ToAccount();
        if (_hintedAccounts.TryGetValue(addressHash, out Account? hinted)) return hinted;
        PbtAccount? account = GetLayeredAccount(addressHash);
        if (promote) PromoteAccount(addressHash, account);
        return account?.ToAccount();
    }

    private PbtAccount? GetLayeredAccount(in ValueHash256 addressHash)
    {
        for (int index = snapshots.Count - 1; index >= 0; index--)
            if (snapshots[index].Content.Accounts.TryGetValue(addressHash, out PbtAccount? account)) return account;
        return readOnlyBundle.GetAccount(addressHash);
    }

    /// <summary>Records an account the layer above read past this bundle; a value already written or hinted here wins, and the memo is dropped with the write buffer at snapshot collection.</summary>
    public void HintAccount(Address address, Account? account) => _hintedAccounts.TryAdd(PbtKeyDerivation.AddressKeyHash(address), account);

    private void PromoteAccount(in ValueHash256 addressHash, PbtAccount? account)
    {
        // ContainsKey is lock-free; TryAdd alone would take the bucket lock on every hot re-promote.
        ConcurrentDictionary<ValueHash256, PbtAccount?> accounts = WriteBuffer.Accounts;
        if (!accounts.ContainsKey(addressHash)) accounts.TryAdd(addressHash, account);
    }

    public EvmWord GetSlot(Address address, in UInt256 slot) => GetSlot(address, PbtKeyDerivation.AddressKeyHash(address), slot);

    /// <inheritdoc cref="GetSlot(Address, in UInt256)"/>
    public EvmWord GetSlot(Address address, in ValueHash256 addressHash, in UInt256 slot)
    {
        HashedKey<PbtStorageTreeKey> runKey = PbtStateKey.StorageRun(address, addressHash, slot, out int index);
        return BufferRun(WriteBuffer, runKey, addressHash).Get(index);
    }

    /// <summary>The run as the write buffer holds it, borrowed; the first touch of a run buffers it as currently visible.</summary>
    private ISlotRun BufferRun(PbtSnapshotContent writeBuffer, in HashedKey<PbtStorageTreeKey> runKey, in ValueHash256 addressHash)
    {
        ISlotRun? run;
        while (!writeBuffer.TryGetSlotRun(runKey, out run))
        {
            run = FindLocalRun(runKey, addressHash)?.Clone() ?? readOnlyBundle.RentRun(runKey, addressHash);
            if (writeBuffer.TryAddRun(runKey, run)) return run;
            SlotRun.Return(run);
        }
        return run;
    }

    public void SetAccount(Address address, Account? account)
    {
        ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(address);
        // The previous value is known as a stem, or as an account when it awaits its code or was hinted.
        PbtAccount? previous = null;
        if (!_accountsAwaitingCode.TryRemove(addressHash, out Account? prior) && !WriteBuffer.Accounts.TryGetValue(addressHash, out previous) &&
            !_hintedAccounts.TryGetValue(addressHash, out prior))
            previous = GetLayeredAccount(addressHash);
        // Code chunk leaves are shared per code hash without a reference count, so a non-delegation code hash
        // can never be replaced or removed once set (EIP-6780 and EIP-161 guarantee this in protocol execution).
        ValueHash256? previousCodeHash = prior is { HasCode: true } ? prior.CodeHash.ValueHash256 : previous is { HasCode: true } stored ? stored.CodeHash : (ValueHash256?)null;
        if (previousCodeHash is { } replaced && replaced != account?.CodeHash.ValueHash256 && !(previous?.IsDelegation ?? IsDelegation(replaced)))
            throw new InvalidOperationException($"The code of {address} cannot be replaced or removed: EIP-8297 code leaves are shared by code hash.");

        if (account is null)
        {
            StoreAccount(addressHash, null);
            SelfDestruct(addressHash);
        }
        else if (previous is { } known && known.CodeHash == account.CodeHash.ValueHash256)
        {
            StoreAccount(addressHash, PbtAccount.From(known, account));
        }
        else
        {
            CodeInfo? code = account.HasCode ? GetCode(account.CodeHash.ValueHash256) : null;
            if (account.HasCode && code is null) _accountsAwaitingCode[addressHash] = account;
            else StoreAccount(addressHash, PbtAccount.From(account, code));
        }
    }

    private bool IsDelegation(in ValueHash256 codeHash) =>
        Eip7702Constants.IsDelegatedCode((GetCode(codeHash) ?? throw new InvalidDataException($"Missing PBT bytecode for {codeHash}.")).CodeSpan);

    private void StoreAccount(in ValueHash256 addressHash, PbtAccount? account)
    {
        WriteAccountLeaves(addressHash, account);
        WriteBuffer.Accounts[addressHash] = account;
    }

    private void WriteAccountLeaves(in ValueHash256 addressHash, PbtAccount? account)
    {
        bool isDelegation = account is { IsDelegation: true };
        SetPbtLeaf(PbtStateKey.Account(addressHash, isDelegation ? (byte)PbtKeyDerivation.DelegationLeafKey : (byte)PbtKeyDerivation.CodeHashLeafKey), account?.CodeLeaf);
        SetPbtLeaf(PbtStateKey.Account(addressHash, isDelegation ? (byte)PbtKeyDerivation.CodeHashLeafKey : (byte)PbtKeyDerivation.DelegationLeafKey), null);
        // A zero basic-data value is stored as a deletion by the batch builder.
        SetPbtLeaf(PbtStateKey.Account(addressHash, PbtKeyDerivation.BasicDataLeafKey), account?.BasicData ?? default);
        // Chunk leaves are keyed by code hash alone, so only code written in this block (see SetCode) stages them;
        // code held by an older layer already has its chunks in the tree.
        if (account is { IsDelegation: false } stem && WriteBuffer.Codes.TryGetValue(stem.CodeLeaf, out CodeInfo? code)) WriteCodeChunkLeaves(stem.CodeLeaf, code);
    }

    /// <summary>Sets a storage slot, reusing a precomputed <see cref="PbtKeyDerivation.AddressKeyHash"/> so a run of slots for one address pays only the per-tree-index suffix hash.</summary>
    public void SetSlot(Address address, in ValueHash256 addressHash, in UInt256 slot, in EvmWord value)
    {
        PbtStorageTreeKey key = PbtStateKey.Storage(address, addressHash, slot);
        SetPbtLeaf(key, EvmWordSlot.IsZero(value) ? null : new ValueHash256(EvmWordSlot.AsReadOnlySpan(in value)));
        HashedKey<PbtStorageTreeKey> runKey = SlotRun.RunKey(key);
        int index = SlotRun.IndexOf(key);
        PbtSnapshotContent writeBuffer = WriteBuffer;
        while (true)
        {
            ISlotRun current = BufferRun(writeBuffer, runKey, addressHash);
            ISlotRun next = current.With(index, value);
            if (writeBuffer.TryReplaceRun(runKey, next, current))
            {
                _replacedRuns.Enqueue(current);
                return;
            }
            SlotRun.Return(next);
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

    // Clearing existing storage (isNewStorage: false) is not supported in PBT: under EIP-6780 a contract only loses
    // its storage when destroyed in its creating transaction, so no persisted run or trie leaf ever needs deleting.
    public void SelfDestruct(in ValueHash256 addressHash) => WriteBuffer.ClearStorage(addressHash, isNewStorage: true);

    /// <summary>Stores bytecode written in this block; its chunk leaves are staged by the account writes that reference it.</summary>
    internal void SetCode(in ValueHash256 codeHash, CodeInfo code)
    {
        WriteBuffer.Codes[codeHash] = code;
        // An account registered after this scan is resolved by PrepareLeafChanges. Removing by address and code hash
        // claims the entry, so an account re-set to other code meanwhile keeps waiting for that code.
        foreach ((ValueHash256 addressHash, Account awaiting) in _accountsAwaitingCode)
            if (awaiting.CodeHash.ValueHash256 == codeHash && _accountsAwaitingCode.TryRemove(new KeyValuePair<ValueHash256, Account>(addressHash, awaiting)))
                StoreAccount(addressHash, PbtAccount.From(awaiting, code));
    }

    private void WriteCodeChunkLeaves(in ValueHash256 codeHash, CodeInfo code)
    {
        foreach ((PbtPath key, ValueHash256 chunk) in PbtFlatState.CodeLeaves(codeHash, code)) SetPbtLeaf(key, chunk);
    }

    /// <summary>The bytecode as a PBT layer holds it; null when no layer has it, in which case its chunk leaves are not in the tree either.</summary>
    internal CodeInfo? GetCode(in ValueHash256 codeHash)
    {
        if (codeHash == ValueKeccak.OfAnEmptyString) return CodeInfo.Empty;
        if (WriteBuffer.Codes.TryGetValue(codeHash, out CodeInfo? code)) return code;
        for (int index = snapshots.Count - 1; index >= 0; index--)
            if (snapshots[index].Content.Codes.TryGetValue(codeHash, out code)) return code;
        if (_codeMemo.TryGetValue(codeHash, out code)) return code;
        code = readOnlyBundle.GetCode(codeHash);
        if (code is not null) _codeMemo[codeHash] = code;
        return code;
    }

    // The owning scope serializes capture with snapshot collection and disposal.
    internal PbtTrieWarmupSession CreateTrieWarmupSession(ITrieWarmer trieWarmer, int sequenceId, ILogger logger = default)
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
            return new PbtTrieWarmupSession(initialSnapshots, readOnlyBundle, _transientResource, trieWarmer, sequenceId, trieNodeCache, logger);
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

    /// <summary>Seals the write buffer into a snapshot and hands the block's transient resource to the caller, who owns its remaining lease.</summary>
    public PbtSnapshot CollectSnapshot(in StateId from, in StateId to, in ValueHash256 treeRoot, out PbtTransientResource retired)
    {
        if (_accountsAwaitingCode.Count != 0 || PendingMutationCount != 0)
            throw new InvalidOperationException("Pending leaf changes must be folded before collecting a snapshot.");
        PbtSnapshot snapshot = new(from, to, treeRoot, WriteBuffer, resourcePool, usage);
        snapshot.TryLease();
        snapshots.Add(snapshot);
        _hintedAccounts.Clear();
        ReturnReplacedRuns();
        _writeBuffer = resourcePool.GetSnapshotContent(usage);
        retired = _transientResource;
        Volatile.Write(ref _transientResource, resourcePool.GetCachedResource(usage));
        return snapshot;
    }

    private void ReturnReplacedRuns()
    {
        while (_replacedRuns.TryDequeue(out ISlotRun? run)) SlotRun.Return(run);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, true)) return;
        _accountsAwaitingCode.Clear();
        _codeMemo.Clear();
        _hintedAccounts.Clear();
        ReturnReplacedRuns();
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
