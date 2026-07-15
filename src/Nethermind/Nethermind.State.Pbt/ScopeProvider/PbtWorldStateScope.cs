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
        PbtWriteBatch batch = new(estimatedEntries: _dirtyAccounts.Count * 2 + 16);
        Dictionary<Stem, byte[]?> priorBlobs = [];
        Dictionary<AddressAsKey, Stem> headerStems = [];

        // self-destructed (including deleted) accounts drop every prior header leaf: basic data,
        // code hash, header slots and header code chunks. Storage-zone stems of pre-existing
        // storage are left in place — clearing them would need a per-address stem index — which is
        // an accepted divergence from a from-scratch merkelization (flat reads stay correct via
        // the self-destruct markers). Emitted before the account/slot writes so re-create wins.
        foreach ((AddressAsKey address, _) in _selfDestructed)
        {
            Stem headerStem = GetHeaderStem(headerStems, address);
            byte[]? prior = GetPriorBlob(priorBlobs, headerStem);
            if (prior is null) continue;

            for (int subIndex = 0; subIndex < PbtKeyDerivation.StemSubtreeWidth; subIndex++)
            {
                if (StemLeafBlob.TryGetValue(prior, (byte)subIndex, out _))
                {
                    batch.Add(PbtKeyDerivation.TreeKey(headerStem, (byte)subIndex), default);
                }
            }
        }

        Span<byte> basicData = stackalloc byte[32];
        foreach ((AddressAsKey address, Account? account) in _dirtyAccounts)
        {
            // deletions were handled by the self-destruct pass
            if (account is null) continue;

            Stem headerStem = GetHeaderStem(headerStems, address);
            byte[]? code = account.HasCode ? CodeDb.GetCode(account.CodeHash) : null;

            PbtKeyDerivation.PackBasicData(basicData, (uint)(code?.Length ?? 0), account.Nonce, account.Balance);
            batch.Add(PbtKeyDerivation.TreeKey(headerStem, PbtKeyDerivation.BasicDataLeafKey), basicData);
            batch.Add(PbtKeyDerivation.TreeKey(headerStem, PbtKeyDerivation.CodeHashLeafKey), account.CodeHash.Bytes);

            if (code is not null && CodeChunksNeeded(address, headerStem, account, priorBlobs))
            {
                byte[][] chunks = PbtKeyDerivation.ChunkifyCode(code);
                int headerChunks = Math.Min(chunks.Length, PbtKeyDerivation.HeaderCodeChunks);
                for (int i = 0; i < headerChunks; i++)
                {
                    batch.Add(PbtKeyDerivation.TreeKey(headerStem, PbtKeyDerivation.HeaderCodeChunkSubIndex(i)), chunks[i]);
                }

                // overflow chunks are content-addressed by code hash and shared between contracts;
                // rewriting existing values is idempotent and they are never deleted
                for (int i = PbtKeyDerivation.HeaderCodeChunks; i < chunks.Length; i++)
                {
                    Stem overflowStem = PbtKeyDerivation.CodeOverflowStem(account.CodeHash, i, out byte subIndex);
                    batch.Add(PbtKeyDerivation.TreeKey(overflowStem, subIndex), chunks[i]);
                }
            }
        }

        foreach ((AddressAsKey address, ConcurrentDictionary<UInt256, EvmWord> slots) in _dirtySlots)
        {
            // slots of an account that ends the block deleted never reach the tree
            if (_dirtyAccounts.TryGetValue(address, out Account? account) && account is null) continue;

            foreach ((UInt256 slot, EvmWord value) in slots)
            {
                Stem stem;
                byte subIndex;
                if (PbtKeyDerivation.IsHeaderSlot(slot))
                {
                    stem = GetHeaderStem(headerStems, address);
                    subIndex = PbtKeyDerivation.HeaderSlotSubIndex(slot);
                }
                else
                {
                    stem = PbtKeyDerivation.StorageStem(address, slot, out subIndex);
                }

                batch.Add(PbtKeyDerivation.TreeKey(stem, subIndex), EvmWordSlot.IsZero(value) ? default : EvmWordSlot.AsReadOnlySpan(in value));
            }
        }

        return batch;
    }

    private bool CodeChunksNeeded(AddressAsKey address, in Stem headerStem, Account account, Dictionary<Stem, byte[]?> priorBlobs)
    {
        // a self-destruct in this block cleared the prior chunks, so they must be rewritten
        if (_selfDestructed.ContainsKey(address)) return true;

        byte[]? prior = GetPriorBlob(priorBlobs, headerStem);
        return prior is null
            || !StemLeafBlob.TryGetValue(prior, PbtKeyDerivation.CodeHashLeafKey, out ReadOnlySpan<byte> priorCodeHash)
            || !priorCodeHash.SequenceEqual(account.CodeHash.Bytes);
    }

    private Stem GetHeaderStem(Dictionary<AddressAsKey, Stem> cache, AddressAsKey address)
    {
        ref Stem stem = ref CollectionsMarshal.GetValueRefOrAddDefault(cache, address, out bool exists);
        if (!exists) stem = PbtKeyDerivation.AccountHeaderStem(address);
        return stem;
    }

    private byte[]? GetPriorBlob(Dictionary<Stem, byte[]?> cache, in Stem stem)
    {
        ref byte[]? blob = ref CollectionsMarshal.GetValueRefOrAddDefault(cache, stem, out bool exists);
        if (!exists) blob = Bundle.GetLeafBlob(stem);
        return blob;
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
