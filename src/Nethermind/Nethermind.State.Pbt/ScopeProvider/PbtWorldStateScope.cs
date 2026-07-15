// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.ScopeProvider;

/// <summary>
/// The read/write surface of one processing branch: flat reads/writes go through the bundle,
/// while <see cref="UpdateRootHash"/> folds the block's cumulative dirty state into an EIP-8297
/// tree-key write batch and hands it to <see cref="TrieUpdater"/> for the root. The scope is the
/// <see cref="IPbtStore"/> the updater reads/writes: reads come from the bundle, writes land in the
/// per-block overlays that <see cref="Commit"/> seals into the snapshot.
/// </summary>
public sealed class PbtWorldStateScope : IWorldStateScopeProvider.IScope, IPbtStore
{
    private readonly IPbtCommitTarget _commitTarget;
    private readonly bool _isReadOnly;

    // cumulative per-block dirty state; slot maps and destruct markers are concurrent because the
    // world state flushes storage write batches from parallel workers
    private readonly Dictionary<AddressAsKey, Account?> _dirtyAccounts = [];
    private readonly ConcurrentDictionary<AddressAsKey, ConcurrentDictionary<UInt256, EvmWord>> _dirtySlots = new();
    private readonly ConcurrentDictionary<AddressAsKey, bool> _selfDestructed = new();
    private readonly Dictionary<ValueHash256, byte[]> _pendingCode = [];
    private readonly Dictionary<AddressAsKey, PbtStorageTree> _storages = [];

    // results of the last UpdateRootHash, sealed into the snapshot at commit
    private readonly Dictionary<Stem, byte[]> _blobOverlay = [];
    private readonly Dictionary<TrieNodeKey, byte[]?> _nodeOverlay = [];

    private StateId _currentStateId;
    private Hash256 _rootHash;
    private bool _rootDirty;

    public PbtWorldStateScope(in StateId currentStateId, PbtSnapshotBundle bundle, IWorldStateScopeProvider.ICodeDb codeDb, IPbtCommitTarget commitTarget, bool isReadOnly)
    {
        _currentStateId = currentStateId;
        Bundle = bundle;
        _commitTarget = commitTarget;
        _isReadOnly = isReadOnly;
        _rootHash = currentStateId.StateRoot.ToHash256();
        CodeDb = new PbtCodeDb(codeDb, _pendingCode);
    }

    internal PbtSnapshotBundle Bundle { get; }

    public Hash256 RootHash => _rootHash;

    public IWorldStateScopeProvider.ICodeDb CodeDb { get; }

    public Account? Get(Address address) => Bundle.GetAccount(address);

    public void HintGet(Address address, Account? account)
    {
    }

    public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) => Task.CompletedTask;

    public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
    {
        ref PbtStorageTree? tree = ref CollectionsMarshal.GetValueRefOrAddDefault(_storages, address, out bool exists);
        if (!exists) tree = new PbtStorageTree(this, address);
        return tree!;
    }

    public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => new WriteBatch(this);

    /// <summary>
    /// Recomputes the root from the block's cumulative dirty state. Idempotent: every call
    /// rebuilds the blob and node overlays from scratch against the pre-block state, so repeated
    /// calls (and calls interleaved with further writes) converge on the same result.
    /// </summary>
    public void UpdateRootHash()
    {
        if (!_rootDirty) return;

        // idempotent: rebuild the overlays from scratch against the pre-block state each call
        _blobOverlay.Clear();
        _nodeOverlay.Clear();

        using PbtWriteBatch changes = BuildChanges();
        _rootHash = TrieUpdater.UpdateRoot(this, _currentStateId.StateRoot, changes).ToHash256();
        _rootDirty = false;
    }

    // ---- IPbtStore: the updater reads prior state from the bundle and writes into the per-block overlays ----

    MemoryManager<byte>? IPbtStore.GetTrieNode(in TrieNodeKey key) => ArrayMemoryManager.From(Bundle.GetTrieNode(key));

    MemoryManager<byte>? IPbtStore.GetLeafBlob(in Stem stem) => ArrayMemoryManager.From(Bundle.GetLeafBlob(stem));

    void IPbtStore.SetTrieNode(in TrieNodeKey key, ReadOnlySpan<byte> node) => _nodeOverlay[key] = node.Length == 0 ? null : node.ToArray();

    void IPbtStore.SetLeafBlob(in Stem stem, ReadOnlySpan<byte> blob) => _blobOverlay[stem] = blob.ToArray();

    public void Commit(ulong blockNumber)
    {
        UpdateRootHash();

        StateId newStateId = new(blockNumber, _rootHash);
        if (newStateId != _currentStateId)
        {
            PbtSnapshot snapshot = Bundle.CollectSnapshot(_currentStateId, newStateId, _blobOverlay, _nodeOverlay);
            if (_isReadOnly)
            {
                // read-only scopes keep the layer only in their own bundle; drop the lease that
                // would have gone to the repository
                snapshot.Dispose();
            }
            else
            {
                _commitTarget.AddSnapshot(snapshot);
            }

            _currentStateId = newStateId;
        }

        _dirtyAccounts.Clear();
        _dirtySlots.Clear();
        _selfDestructed.Clear();
        _pendingCode.Clear();
        _blobOverlay.Clear();
        _nodeOverlay.Clear();
        _storages.Clear();
        _rootDirty = false;
    }

    public void Dispose() => Bundle.Dispose();

    /// <summary>
    /// Derives the EIP-8297 tree-key writes of the block into a <see cref="PbtWriteBatch"/>: header
    /// stems from dirty accounts (with code mirrored as chunks), storage stems from dirty slots, and
    /// clears from self-destructs. Values are fed as spans; the batch owns the copied blob.
    /// </summary>
    private PbtWriteBatch BuildChanges()
    {
        PbtWriteBatch batch = new(estimatedStems: _dirtyAccounts.Count + 16);
        HashSet<ValueHash256> overflowCodes = [];
        Span<byte> basicData = stackalloc byte[32];

        // One pass per touched address so every stem is built completely before it is added — the
        // header stem gathers self-destruct clears, account data, code chunks and header slots, and
        // storage slots are grouped by their storage stem. Content-addressed overflow code stems are
        // shared across accounts, so they are emitted once per code hash.
        foreach (AddressAsKey address in TouchedAddresses())
        {
            Stem headerStem = PbtKeyDerivation.AccountHeaderStem(address);
            _dirtyAccounts.TryGetValue(address, out Account? account);
            bool deleted = account is null && _dirtyAccounts.ContainsKey(address);
            _dirtySlots.TryGetValue(address, out ConcurrentDictionary<UInt256, EvmWord>? slots);

            Dictionary<byte, ValueHash256> header = [];

            // self-destructed (including deleted) accounts drop every prior header leaf; the
            // storage-zone stems of pre-existing storage are left stale (an accepted divergence).
            // Written before the account data so a same-block re-create wins per sub-index.
            if (_selfDestructed.ContainsKey(address))
            {
                byte[]? prior = Bundle.GetLeafBlob(headerStem);
                if (prior is not null)
                {
                    for (int subIndex = 0; subIndex < PbtKeyDerivation.StemSubtreeWidth; subIndex++)
                    {
                        if (StemLeafBlob.TryGetValue(prior, (byte)subIndex, out _)) header[(byte)subIndex] = default;
                    }
                }
            }

            if (account is not null)
            {
                // Code (immutable after creation) is only chunked when it was written this block; its
                // hash then identifies the pending bytes. For an unchanged-code account whose balance
                // or nonce changed we still rewrite BASIC_DATA, so its code size is read back from the
                // prior BASIC_DATA leaf rather than fetching the whole code.
                byte[]? updatedCode = account.HasCode && _pendingCode.TryGetValue(account.CodeHash, out byte[]? c) ? c : null;
                uint codeSize = updatedCode is not null ? (uint)updatedCode.Length
                    : !account.HasCode ? 0
                    : PriorCodeSize(headerStem);

                PbtKeyDerivation.PackBasicData(basicData, codeSize, account.Nonce, account.Balance);
                header[PbtKeyDerivation.BasicDataLeafKey] = ToLeaf(basicData);
                header[PbtKeyDerivation.CodeHashLeafKey] = ToLeaf(account.CodeHash.Bytes);

                if (updatedCode is not null)
                {
                    byte[][] chunks = PbtKeyDerivation.ChunkifyCode(updatedCode);
                    int headerChunks = Math.Min(chunks.Length, PbtKeyDerivation.HeaderCodeChunks);
                    for (int i = 0; i < headerChunks; i++)
                    {
                        header[PbtKeyDerivation.HeaderCodeChunkSubIndex(i)] = ToLeaf(chunks[i]);
                    }

                    // overflow chunks are content-addressed and shared; emit each code's once
                    if (chunks.Length > PbtKeyDerivation.HeaderCodeChunks && overflowCodes.Add(account.CodeHash))
                    {
                        AddOverflowChunks(batch, account.CodeHash, chunks);
                    }
                }
            }

            // header storage slots (0..63); a deleted account's slots never reach the tree
            if (!deleted && slots is not null)
            {
                foreach ((UInt256 slot, EvmWord value) in slots)
                {
                    if (PbtKeyDerivation.IsHeaderSlot(slot)) header[PbtKeyDerivation.HeaderSlotSubIndex(slot)] = SlotLeaf(value);
                }
            }

            if (header.Count > 0) batch.Add(headerStem, header);

            // storage-zone slots (64+), grouped by their storage stem
            if (!deleted && slots is not null)
            {
                Dictionary<Stem, Dictionary<byte, ValueHash256>> storage = [];
                foreach ((UInt256 slot, EvmWord value) in slots)
                {
                    if (PbtKeyDerivation.IsHeaderSlot(slot)) continue;

                    Stem stem = PbtKeyDerivation.StorageStem(address, slot, out byte subIndex);
                    if (!storage.TryGetValue(stem, out Dictionary<byte, ValueHash256>? stemChanges)) storage[stem] = stemChanges = [];
                    stemChanges[subIndex] = SlotLeaf(value);
                }

                foreach ((Stem stem, Dictionary<byte, ValueHash256> stemChanges) in storage) batch.Add(stem, stemChanges);
            }
        }

        return batch;
    }

    /// <summary>The distinct addresses touched this block: dirty accounts, self-destructs and dirty storage.</summary>
    private HashSet<AddressAsKey> TouchedAddresses()
    {
        HashSet<AddressAsKey> addresses = new(_dirtyAccounts.Count + _dirtySlots.Count);
        foreach ((AddressAsKey address, _) in _dirtyAccounts) addresses.Add(address);
        foreach ((AddressAsKey address, _) in _selfDestructed) addresses.Add(address);
        foreach ((AddressAsKey address, _) in _dirtySlots) addresses.Add(address);
        return addresses;
    }

    /// <summary>Adds a code's overflow chunks (index 128+), grouped by their content-addressed code-zone stem.</summary>
    private static void AddOverflowChunks(PbtWriteBatch batch, in ValueHash256 codeHash, byte[][] chunks)
    {
        Dictionary<Stem, Dictionary<byte, ValueHash256>> overflow = [];
        for (int i = PbtKeyDerivation.HeaderCodeChunks; i < chunks.Length; i++)
        {
            Stem stem = PbtKeyDerivation.CodeOverflowStem(codeHash, i, out byte subIndex);
            if (!overflow.TryGetValue(stem, out Dictionary<byte, ValueHash256>? stemChanges)) overflow[stem] = stemChanges = [];
            stemChanges[subIndex] = ToLeaf(chunks[i]);
        }

        foreach ((Stem stem, Dictionary<byte, ValueHash256> stemChanges) in overflow) batch.Add(stem, stemChanges);
    }

    private static ValueHash256 ToLeaf(ReadOnlySpan<byte> value)
    {
        ValueHash256 leaf = default;
        value.CopyTo(leaf.BytesAsSpan);
        return leaf;
    }

    private static ValueHash256 SlotLeaf(in EvmWord value) =>
        EvmWordSlot.IsZero(value) ? default : new ValueHash256(EvmWordSlot.AsReadOnlySpan(in value));

    /// <summary>Reads the code size preserved in the account's prior <c>BASIC_DATA</c> leaf (0 if the account is new).</summary>
    private uint PriorCodeSize(in Stem headerStem)
    {
        byte[]? prior = Bundle.GetLeafBlob(headerStem);
        return prior is not null && StemLeafBlob.TryGetValue(prior, PbtKeyDerivation.BasicDataLeafKey, out ReadOnlySpan<byte> basicData)
            ? PbtKeyDerivation.ReadBasicDataCodeSize(basicData)
            : 0;
    }

    private void SetSlot(Address address, in UInt256 slot, in EvmWord value)
    {
        Bundle.SetSlot(address, slot, value);
        _rootDirty = true;
    }

    private void SelfDestructStorage(Address address)
    {
        _selfDestructed[address] = true;
        _dirtySlots.TryRemove(address, out _);
        Bundle.SelfDestruct(address);
        _rootDirty = true;
    }

    private sealed class WriteBatch(PbtWorldStateScope scope) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        // PBT accounts have no storage root to fold back into the state provider, so the event is never raised
        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated
        {
            add
            {
            }
            remove
            {
            }
        }

        public void Set(Address key, Account? account)
        {
            scope._dirtyAccounts[key] = account;
            scope.Bundle.SetAccount(key, account);
            scope._rootDirty = true;

            // the world state skips the storage write batch entirely for removed accounts, so the
            // storage clear must happen here
            if (account is null) scope.SelfDestructStorage(key);
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
            new StorageWriteBatch(scope, key, scope._dirtySlots.GetOrAdd(key, static _ => new ConcurrentDictionary<UInt256, EvmWord>()));

        public void Dispose()
        {
        }
    }

    private sealed class StorageWriteBatch(PbtWorldStateScope scope, Address address, ConcurrentDictionary<UInt256, EvmWord> slots) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Set(in UInt256 index, byte[] value)
        {
            // the world state passes a stripped (leading-zeros-removed) value; canonicalize to 32 bytes
            EvmWord word = EvmWordSlot.FromStripped(value);
            slots[index] = word;
            scope.SetSlot(address, index, word);
        }

        public void Clear()
        {
            slots.Clear();
            scope.SelfDestructStorage(address);
            // reattach the (now empty) map so writes after the clear still register
            scope._dirtySlots[address] = slots;
        }

        public void Dispose()
        {
        }
    }
}
