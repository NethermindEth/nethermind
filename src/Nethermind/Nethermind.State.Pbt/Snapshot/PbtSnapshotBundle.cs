// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Extensions;
using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.ScopeProvider;

namespace Nethermind.State.Pbt.Snapshot;

/// <summary>A writable flat branch over sealed local snapshots and a shared read-only base.</summary>
/// <param name="filterInMemorySlotReads">Serve run reads that reach the read-only base with
/// <see cref="PbtReadOnlySnapshotBundle.RentRunFiltered"/>; for read-only execution only.</param>
public sealed class PbtSnapshotBundle(
    PbtSnapshotPooledList snapshots,
    PbtReadOnlySnapshotBundle readOnlyBundle,
    IPbtGroupRecordBuffer groupRecordBuffer,
    IPbtResourcePool resourcePool,
    PbtResourcePool.Usage usage,
    bool filterInMemorySlotReads) : IDisposable
{
    private PbtSnapshotContent? _writeBuffer = RentWriteBuffer(resourcePool, usage, groupRecordBuffer);
    // Taken before the first root index lookup and after the write buffer's generation pin, so with it this covers an
    // indexed root written after the read-only base was pinned.
    private IDisposable? _rootLookupPin;
    private readonly PbtPartitionBatchesBuilder _leafChanges = new(
        resourcePool.GetWriteBatch(usage), resourcePool.GetWriteBatch(usage), resourcePool.GetStorageWriteBatch(usage));
    // Accounts set before their code arrived; they reach the write buffer once the code converts them to their stem.
    private readonly ConcurrentDictionary<ValueHash256, Account> _accountsAwaitingCode = new();
    // Read-through memo of bytecode served by the read-only base; never snapshot content, so it is not persisted.
    private readonly ConcurrentDictionary<ValueHash256, CodeInfo> _codeMemo = new();
    // Accounts the layer above read past this bundle for the block in the write buffer, so a write never re-reads them.
    private readonly ConcurrentDictionary<ValueHash256, Account?> _hintedAccounts = new();
    // Persisted groups, and null for absent ones, that the fold or the node group prefetch read for the block in the write buffer.
    private readonly ConcurrentDictionary<PbtStorageNodePath, RefCountingMemory?> _persistedNodeGroups = new();
    // Storage commits may write one run from several threads, so a write replaces its run by compare-and-swap.
    // A replaced run is held here until the write buffer is sealed: returned earlier, it could be re-rented and
    // stored back under the same key, and a writer still comparing against it would overwrite a newer run (ABA).
    private readonly ConcurrentQueue<PackedSlotRun> _replacedRuns = new();
    private bool _isDisposed;
    // A base that can never have a filter would only pay RentRunFiltered's extra checks on every read.
    private readonly bool _filterInMemorySlotReads = filterInMemorySlotReads && readOnlyBundle.MayFilterSlots;

    public ValueHash256 TreeRoot => snapshots.Count > 0 ? snapshots[^1].TreeRoot : readOnlyBundle.TreeRoot;

    /// <summary>The group record buffer of this branch's write buffers and local snapshots.</summary>
    public IPbtGroupRecordBuffer GroupRecordBuffer => groupRecordBuffer;

    /// <summary>The last published root reference visible to this branch, an indexed persisted root, or an unresolved persisted root.</summary>
    public PbtGroupReference RootGroupReference
    {
        get
        {
            if (!WriteBuffer.RootGroupReference.IsUnknown) return WriteBuffer.RootGroupReference;
            if (snapshots.Count > 0) return snapshots[^1].MapRootGroupReference(GroupRecordBuffer);
            if (Volatile.Read(ref _rootLookupPin) is null)
            {
                IDisposable pin = GroupRecordBuffer.PinAll();
                if (Interlocked.CompareExchange(ref _rootLookupPin, pin, null) is not null) pin.Dispose();
            }
            return readOnlyBundle.GetRootGroupReference(GroupRecordBuffer);
        }
    }

    private PbtSnapshotContent WriteBuffer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return _writeBuffer!;
        }
    }

    public int PendingMutationCount => _leafChanges.Count;

    private void SetPbtLeaf(PbtPath key, ValueHash256? value)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        _leafChanges.SetLeaf(key, value);
    }

    private void SetPbtLeaf(in PbtStoragePath key, ValueHash256? value)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        _leafChanges.SetLeaf(key, value);
    }

    public PbtPartitionBatches PrepareLeafChanges()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        foreach ((ValueHash256 addressHash, Account awaiting) in _accountsAwaitingCode)
        {
            CodeInfo code = GetCode(awaiting.CodeHash.ValueHash256) ?? throw new InvalidDataException($"Missing PBT bytecode for {awaiting.CodeHash}.");
            StoreAccount(addressHash, PbtAccount.From(awaiting, code), code);
        }
        _accountsAwaitingCode.Clear();
        return _leafChanges.Build();
    }

    public void CompleteLeafChanges() => _leafChanges.Reset();

    /// <summary>Publishes a completed fold's root group from this bundle's group record buffer and accounts for the groups the fold wrote.</summary>
    /// <param name="root">The new tree's root group, or None for an empty tree.</param>
    /// <param name="count">The number of groups the fold wrote.</param>
    /// <param name="bytes">Their path and payload bytes.</param>
    public void PublishNodeGroups(PbtGroupReference root, int count, long bytes)
    {
        WriteBuffer.RootGroupReference = root;
        WriteBuffer.AddNodeGroups(count, bytes);
    }

    public RefCountingMemory? GetNodeGroup(PbtStorageNodePath groupKey) =>
        TryGetSnapshotNodeGroup(groupKey, out RefCountingMemory? payload) ? payload : GetPersistedNodeGroup(groupKey);

    /// <summary>Returns a caller-owned group lease, or null when the branch's tree has no group there, from its resident tree or the persisted snapshots.</summary>
    /// <returns>False when neither resolves the group, which the persistence reader must then provide.</returns>
    public bool TryGetSnapshotNodeGroup(PbtStorageNodePath groupKey, out RefCountingMemory? payload) =>
        WriteBuffer.TryFindNodeGroup(RootGroupReference, groupKey, out payload) || readOnlyBundle.TryGetSnapshotNodeGroup(groupKey, out payload);

    /// <summary>Reads only persisted layers, keeping the result for this block's later misses.</summary>
    public RefCountingMemory? GetPersistedNodeGroup(PbtStorageNodePath groupKey) =>
        TryGetKeptNodeGroup(groupKey, out RefCountingMemory? payload) ? payload : ReadPersistedNodeGroup(groupKey);

    private bool TryGetKeptNodeGroup(PbtStorageNodePath groupKey, out RefCountingMemory? payload)
    {
        if (!_persistedNodeGroups.TryGetValue(groupKey, out payload)) return false;
        payload?.AcquireLease();
        return true;
    }

    private RefCountingMemory? ReadPersistedNodeGroup(PbtStorageNodePath groupKey)
    {
        RefCountingMemory? payload = readOnlyBundle.GetPersistedNodeGroup(groupKey);
        payload?.AcquireLease();
        if (!_persistedNodeGroups.TryAdd(groupKey, payload)) ((IDisposable?)payload)?.Dispose();
        return payload;
    }

    private void ReleasePersistedNodeGroups()
    {
        foreach (KeyValuePair<PbtStorageNodePath, RefCountingMemory?> entry in _persistedNodeGroups) ((IDisposable?)entry.Value)?.Dispose();
        _persistedNodeGroups.Clear();
    }

    public Account? GetAccount(Address address) => ReadAccount(Eip8297KeyDerivation.AddressHash(address.ToHash().Bytes), promote: false);

    /// <summary>Reads an account, promoting one found past the write buffer and the hint memo into the write buffer.</summary>
    /// <remarks>The promoted account is sealed into the next snapshot, so later heads read it from the newest layer.</remarks>
    public Account? GetAndPromoteAccount(Address address) => ReadAccount(Eip8297KeyDerivation.AddressHash(address.ToHash().Bytes), promote: true);

    public Account? ReadAccount(in ValueHash256 addressHash, bool promote)
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
    public void HintAccount(Address address, Account? account) => _hintedAccounts.TryAdd(Eip8297KeyDerivation.AddressHash(address.ToHash().Bytes), account);

    private void PromoteAccount(in ValueHash256 addressHash, PbtAccount? account)
    {
        // ContainsKey is lock-free; TryAdd alone would take the bucket lock on every hot re-promote.
        ConcurrentDictionary<ValueHash256, PbtAccount?> accounts = WriteBuffer.Accounts;
        if (!accounts.ContainsKey(addressHash)) accounts.TryAdd(addressHash, account);
    }

    public UInt256 GetSlot(Address address, in UInt256 slot) => GetSlot(address, Eip8297KeyDerivation.AddressHash(address.ToHash().Bytes), slot);

    /// <inheritdoc cref="GetSlot(Address, in UInt256)"/>
    public UInt256 GetSlot(Address address, in ValueHash256 addressHash, in UInt256 slot) => Eip8297KeyDerivation.IsHeaderSlot(slot)
        ? GetSlot(Eip8297KeyDerivation.HeaderStorageKey(addressHash, slot), addressHash)
        : GetSlot(Eip8297KeyDerivation.StorageKey(address.ToHash().Bytes, addressHash, slot), addressHash);

    public UInt256 GetSlot<TKey>(in TKey slotKey, in ValueHash256 addressHash) where TKey : struct, IPbtKey<TKey> =>
        BufferRun<TKey>(WriteBuffer, SlotRun.RunKey(slotKey), addressHash).Get(SlotRun.IndexOf(slotKey));

    /// <summary>The run as the write buffer holds it, borrowed; the first touch of a run buffers it as currently visible.</summary>
    private PackedSlotRun BufferRun<TKey>(PbtSnapshotContent writeBuffer, in HashedKey<TKey> runKey, in ValueHash256 addressHash) where TKey : struct, IPbtKey<TKey>
    {
        PackedSlotRun? run;
        while (!writeBuffer.TryGetSlotRun(runKey, out run))
        {
            run = FindLocalRun(runKey, addressHash)?.Clone()
                ?? (_filterInMemorySlotReads ? readOnlyBundle.RentRunFiltered(runKey, addressHash) : readOnlyBundle.RentRun(runKey, addressHash));
            if (writeBuffer.TryAddRun(runKey, run)) return run;
            SlotRun.Return(run);
        }
        return run;
    }

    public void SetAccount(Address address, Account? account)
    {
        ValueHash256 addressHash = Eip8297KeyDerivation.AddressHash(address.ToHash().Bytes);
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
            StoreAccount(addressHash, null, null);
            SelfDestruct(addressHash);
        }
        else if (previous is { } known && known.CodeHash == account.CodeHash.ValueHash256)
        {
            StoreAccount(addressHash, known.WithNonceAndBalance(account), null);
        }
        else
        {
            CodeInfo? code = account.HasCode ? GetCode(account.CodeHash.ValueHash256) : null;
            if (account.HasCode && code is null) _accountsAwaitingCode[addressHash] = account;
            else StoreAccount(addressHash, PbtAccount.From(account, code), previousCodeHash is null ? code : null);
        }
    }

    private bool IsDelegation(in ValueHash256 codeHash) =>
        Eip7702Constants.IsDelegatedCode((GetCode(codeHash) ?? throw new InvalidDataException($"Missing PBT bytecode for {codeHash}.")).CodeSpan);

    private void StoreAccount(in ValueHash256 addressHash, PbtAccount? account, CodeInfo? deployedCode)
    {
        WriteAccountLeaves(addressHash, account, deployedCode);
        WriteBuffer.Accounts[addressHash] = account;
    }

    private void WriteAccountLeaves(in ValueHash256 addressHash, PbtAccount? account, CodeInfo? deployedCode)
    {
        bool isDelegation = account is { IsDelegation: true };
        SetPbtLeaf(Eip8297KeyDerivation.AccountKey(addressHash, isDelegation ? (byte)PbtKeyDerivation.DelegationLeafKey : (byte)PbtKeyDerivation.CodeHashLeafKey), account?.CodeLeaf);
        SetPbtLeaf(Eip8297KeyDerivation.AccountKey(addressHash, isDelegation ? (byte)PbtKeyDerivation.CodeHashLeafKey : (byte)PbtKeyDerivation.DelegationLeafKey), null);
        // A zero basic-data value is stored as a deletion by the batch builder.
        SetPbtLeaf(Eip8297KeyDerivation.AccountKey(addressHash, PbtKeyDerivation.BasicDataLeafKey), account?.BasicData ?? default);
        // Chunk leaves are keyed by code hash alone, so every deployment stages them: a layer can hold the bytecode
        // without its chunks in the tree, as when the contract that first wrote it did not survive its transaction.
        if (account is { IsDelegation: false } stem && deployedCode is not null) WriteCodeChunkLeaves(stem.CodeLeaf, deployedCode);
    }

    /// <summary>Sets storage slots of one address, reusing a precomputed <see cref="Eip8297KeyDerivation.AddressHash"/> so a run of slots for one address pays only the per-tree-index suffix hash.</summary>
    /// <remarks>
    /// Writes are applied in order, so a later write of a slot wins. Adjacent writes into one slot run rewrite that
    /// run once, so writes sorted by slot rewrite each run they touch once.
    /// </remarks>
    public void SetSlots(Address address, in ValueHash256 addressHash, ReadOnlySpan<(UInt256 Slot, UInt256 Value)> writes)
    {
        for (int start = 0, end; start < writes.Length; start = end)
        {
            for (end = start + 1; end < writes.Length && SlotRun.InSameRun(writes[start].Slot, writes[end].Slot); end++) { }
            ReadOnlySpan<(UInt256 Slot, UInt256 Value)> runWrites = writes[start..end];
            if (Eip8297KeyDerivation.IsHeaderSlot(runWrites[0].Slot))
            {
                PbtPath key = default;
                foreach ((UInt256 Slot, UInt256 Value) write in runWrites) SetPbtLeaf(key = Eip8297KeyDerivation.HeaderStorageKey(addressHash, write.Slot), SlotLeaf(write.Value));
                SetRunSlots(key, addressHash, runWrites);
            }
            else
            {
                // A run lies within one stem, so only its first key is hashed.
                PbtStoragePath key = Eip8297KeyDerivation.StorageKey(address.ToHash().Bytes, addressHash, runWrites[0].Slot);
                foreach ((UInt256 Slot, UInt256 Value) write in runWrites) SetPbtLeaf(key = Eip8297KeyDerivation.StorageKeyInStem(key, write.Slot), SlotLeaf(write.Value));
                SetRunSlots(key, addressHash, runWrites);
            }
        }
    }

    private static ValueHash256? SlotLeaf(in UInt256 value) => value.IsZero ? null : value.ToValueHash();

    /// <summary>Applies <paramref name="runWrites"/>, all into the run of <paramref name="slotKey"/>, with one rewrite of that run.</summary>
    private void SetRunSlots<TKey>(in TKey slotKey, in ValueHash256 addressHash, ReadOnlySpan<(UInt256 Slot, UInt256 Value)> runWrites) where TKey : struct, IPbtKey<TKey>
    {
        HashedKey<TKey> runKey = SlotRun.RunKey(slotKey);
        PbtSnapshotContent writeBuffer = WriteBuffer;
        while (true)
        {
            PackedSlotRun current = BufferRun(writeBuffer, runKey, addressHash);
            PackedSlotRun next = current.With(runWrites);
            if (writeBuffer.TryReplaceRun(runKey, next, current))
            {
                _replacedRuns.Enqueue(current);
                return;
            }
            SlotRun.Return(next);
        }
    }

    /// <summary>The run as the local snapshots see it, borrowed; null when they say nothing about it.</summary>
    private PackedSlotRun? FindLocalRun<TKey>(in HashedKey<TKey> runKey, in ValueHash256 addressHash) where TKey : struct, IPbtKey<TKey>
    {
        if (WriteBuffer.SelfDestructedStorageAddresses.ContainsKey(addressHash)) return SlotRun.Empty;
        for (int layer = snapshots.Count - 1; layer >= 0; layer--)
        {
            PbtSnapshotContent content = snapshots[layer].Content;
            if (content.TryGetSlotRun(runKey, out PackedSlotRun? run)) return run;
            if (content.SelfDestructedStorageAddresses.ContainsKey(addressHash)) return SlotRun.Empty;
        }
        return null;
    }

    // Clearing persisted storage is not supported in PBT: under EIP-6780 a contract only loses its storage when
    // destroyed in its creating transaction, so no persisted run or trie leaf ever needs deleting.
    public void SelfDestruct(in ValueHash256 addressHash) => WriteBuffer.ClearStorage(addressHash);

    /// <summary>Stores bytecode written in this block; its chunk leaves are staged by the account writes that reference it.</summary>
    public void SetCode(in ValueHash256 codeHash, CodeInfo code)
    {
        WriteBuffer.Codes[codeHash] = code;
        // An account registered after this scan is resolved by PrepareLeafChanges. Removing by address and code hash
        // claims the entry, so an account re-set to other code meanwhile keeps waiting for that code.
        foreach ((ValueHash256 addressHash, Account awaiting) in _accountsAwaitingCode)
            if (awaiting.CodeHash.ValueHash256 == codeHash && _accountsAwaitingCode.TryRemove(new KeyValuePair<ValueHash256, Account>(addressHash, awaiting)))
                StoreAccount(addressHash, PbtAccount.From(awaiting, code), code);
    }

    private void WriteCodeChunkLeaves(in ValueHash256 codeHash, CodeInfo code)
    {
        foreach ((PbtPath key, ValueHash256 chunk) in Eip8297KeyDerivation.CodeLeaves(codeHash, code.Code)) SetPbtLeaf(key, chunk);
    }

    /// <summary>The bytecode as a PBT layer holds it; null when no layer has it, in which case its chunk leaves are not in the tree either.</summary>
    public CodeInfo? GetCode(in ValueHash256 codeHash)
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

    /// <summary>Seals the write buffer into a snapshot and hands its initial lease to the caller.</summary>
    /// <remarks>
    /// The snapshot's root group reference is None only for the empty tree: a root group this branch's buffer cannot reach is
    /// copied back in.
    /// </remarks>
    public PbtSnapshot CollectSnapshot(in StateId from, in StateId to, in ValueHash256 treeRoot)
    {
        if (_accountsAwaitingCode.Count != 0 || PendingMutationCount != 0)
            throw new InvalidOperationException("Pending leaf changes must be folded before collecting a snapshot.");
        PbtGroupReference rootReference = WriteBuffer.RootGroupReference;
        if (rootReference.IsUnknown)
        {
            if (treeRoot == default) rootReference = PbtGroupReference.None;
            else if (treeRoot != TreeRoot) throw new InvalidOperationException("The tree root changed without a published root group.");
            else rootReference = RootGroupReference.IsDirect ? RootGroupReference : CopyRootGroup();
        }
        if (rootReference.IsDirect) GroupRecordBuffer.SetRoot(treeRoot, rootReference);
        WriteBuffer.RootGroupReference = rootReference;
        PbtSnapshot snapshot = new(from, to, treeRoot, WriteBuffer, resourcePool, usage);
        snapshot.TryLease();
        snapshots.Add(snapshot);
        if (GroupRecordBuffer is PbtGroupRecordBuffer buffer) Metrics.ObserveGroupRecordBuffer(buffer);
        _hintedAccounts.Clear();
        ReleasePersistedNodeGroups();
        ReturnReplacedRuns();
        _writeBuffer = RentWriteBuffer(resourcePool, usage, groupRecordBuffer);
        return snapshot;
    }

    /// <exception cref="InvalidDataException">No layer holds a root group for the non-empty tree.</exception>
    private PbtGroupReference CopyRootGroup()
    {
        using RefCountingMemory payload = GetNodeGroup(default) ?? throw new InvalidDataException("A referenced PBT node group is missing.");
        ReadOnlySpan<byte> bytes = payload.GetSpan();
        PbtGroupReference reference = GroupRecordBuffer.Append(default, bytes, PbtGroupLinks.Unknown);
        WriteBuffer.AddNodeGroups(1, bytes.Length);
        return reference;
    }

    private static PbtSnapshotContent RentWriteBuffer(IPbtResourcePool resourcePool, PbtResourcePool.Usage usage, IPbtGroupRecordBuffer groupRecordBuffer)
    {
        PbtSnapshotContent content = resourcePool.GetSnapshotContent(usage);
        content.BeginNodeGroupWrites(groupRecordBuffer);
        return content;
    }

    private void ReturnReplacedRuns()
    {
        while (_replacedRuns.TryDequeue(out PackedSlotRun? run)) SlotRun.Return(run);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, true)) return;
        _accountsAwaitingCode.Clear();
        _codeMemo.Clear();
        _hintedAccounts.Clear();
        ReleasePersistedNodeGroups();
        ReturnReplacedRuns();
        Interlocked.Exchange(ref _rootLookupPin, null)?.Dispose();
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
                    resourcePool.ReturnWriteBatch(usage, _leafChanges.Account);
                }
                finally
                {
                    try
                    {
                        resourcePool.ReturnWriteBatch(usage, _leafChanges.Code);
                    }
                    finally
                    {
                        resourcePool.ReturnStorageWriteBatch(usage, _leafChanges.Storage);
                    }
                }
            }
            finally
            {
                readOnlyBundle.Dispose();
            }
        }
    }
}
