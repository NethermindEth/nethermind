// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
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
/// <see cref="IPbtStore"/> the updater reads/writes: both go through the bundle's write buffer, which
/// shadows the layer chain, so a fold composes on top of any earlier fold this block and
/// <see cref="Commit"/> seals the accumulated buffer into the snapshot.
/// </summary>
public sealed class PbtWorldStateScope : IWorldStateScopeProvider.IScope, IPbtStore
{
    private readonly IPbtCommitTarget _commitTarget;
    private readonly bool _isReadOnly;

    // stem leaves dirtied since the last root update, keyed by their EIP-8297 stem and grouped at
    // write time: storage slots from the parallel storage batches and account/code header leaves from
    // the single-threaded account flush both land here (their sub-index bands never overlap). The outer
    // map is concurrent because parallel storage workers add new stems, but each stem is
    // address-derived and written by a single worker, so its per-stem change map is single-writer.
    // UpdateRootHash drains this into the write batch, which returns the pooled maps to the pool.
    private readonly ConcurrentDictionary<Stem, IPbtStemChanges> _dirtyStems = new();
    private readonly Dictionary<ValueHash256, byte[]> _pendingCode = [];
    private readonly Dictionary<AddressAsKey, PbtStorageTree> _storages = [];

    private StateId _currentStateId;
    private Hash256 _rootHash;
    private bool _rootDirty;
    private bool _isDisposed;

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
    /// Folds the stems dirtied since the last update into the tree on top of the pre-block state,
    /// recording the new blobs and nodes in the bundle's write buffer and flushing the dirty set. A
    /// repeated call with no intervening writes is a no-op; <see cref="Commit"/> seals the buffer.
    /// </summary>
    public void UpdateRootHash()
    {
        if (!_rootDirty) return;

        using PbtWriteBatch changes = BuildChanges();
        _rootHash = TrieUpdater.UpdateRoot(this, _currentStateId.StateRoot, changes, PooledRefCountingMemoryProvider.Instance).ToHash256();
        _rootDirty = false;
    }

    // ---- IPbtStore: the updater's reads and writes both go through the bundle, whose write buffer is
    // probed ahead of the layer chain, so a fold composes on top of any earlier fold this block ----

    RefCountingMemory? IPbtStore.GetTrieNode(in TrieNodeKey key) => Bundle.GetTrieNode(key);

    RefCountingMemory? IPbtStore.GetLeafBlob(in Stem stem) => Bundle.GetLeafBlob(stem);

    void IPbtStore.SetTrieNode(in TrieNodeKey key, RefCountingMemory? node) => Bundle.SetTrieNode(key, node.ToArrayAndRelease());

    // an emptied stem is buffered as an empty blob: the tombstone that shadows the layer chain's blob
    void IPbtStore.SetLeafBlob(in Stem stem, RefCountingMemory? blob) => Bundle.SetLeafBlob(stem, blob.ToArrayAndRelease() ?? []);

    public void Commit(ulong blockNumber)
    {
        UpdateRootHash();

        StateId newStateId = new(blockNumber, _rootHash);
        if (newStateId != _currentStateId)
        {
            PbtSnapshot snapshot = Bundle.CollectSnapshot(_currentStateId, newStateId);
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

        // _dirtyStems was already drained (and its maps returned) by UpdateRootHash, and the blob and
        // node results left with the buffer CollectSnapshot sealed
        _pendingCode.Clear();
        _storages.Clear();
        _rootDirty = false;
    }

    /// <remarks>
    /// Returns the stem-change maps still held by <see cref="_dirtyStems"/>: a scope abandoned with
    /// pending writes (an exception mid-block, or a branch dropped before its final
    /// <see cref="UpdateRootHash"/>) never reaches <see cref="BuildChanges"/>, and the maps would
    /// otherwise be lost to the GC rather than returned — a silent drain of the shared pool.
    /// Idempotent, so a double dispose cannot return a map twice.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;

        try
        {
            // only maps BuildChanges did not already hand to a write batch are still here: it drains
            // and clears in one step, so ownership is never held in both places
            foreach ((_, IPbtStemChanges changes) in _dirtyStems)
            {
                PbtStemChanges.Return(changes);
            }
        }
        finally
        {
            _dirtyStems.NoLockClear();
            Bundle.Dispose();
        }
    }

    /// <summary>
    /// Folds an account's EIP-8297 header leaves straight into <see cref="_dirtyStems"/>: BASIC_DATA,
    /// CODE_HASH and the header code chunks on its header stem, plus any overflow chunks on their
    /// content-addressed code-zone stems. Runs on the single-threaded account flush, after the storage
    /// batches, so it merges with any header-region slots already present on the header stem (their
    /// sub-index bands never overlap).
    /// </summary>
    private void ApplyAccountHeader(Address address, Account account)
    {
        Stem headerStem = PbtKeyDerivation.AccountHeaderStem(address);

        // Code (immutable after creation) is only chunked when it was written this block; its hash
        // then identifies the pending bytes. For an unchanged-code account whose balance or nonce
        // changed we still rewrite BASIC_DATA, so its code size is read back from the prior BASIC_DATA
        // leaf rather than fetching the whole code.
        byte[]? updatedCode = account.HasCode && _pendingCode.TryGetValue(account.CodeHash, out byte[]? c) ? c : null;
        uint codeSize = updatedCode is not null ? (uint)updatedCode.Length
            : !account.HasCode ? 0
            : PriorCodeSize(headerStem);
        byte[][]? chunks = updatedCode is null ? null : PbtKeyDerivation.ChunkifyCode(updatedCode);

        // Set may promote the pooled map to a larger variant, returning the old one to the pool, so
        // this account's leaves accumulate on a local and the store-back happens in the finally:
        // leaving a promoted-away map in the dictionary would hand a pooled map two owners.
        IPbtStemChanges header = _dirtyStems.GetOrAdd(headerStem, static _ => PbtStemChanges.Rent());
        try
        {
            Span<byte> basicData = stackalloc byte[32];
            PbtKeyDerivation.PackBasicData(basicData, codeSize, account.Nonce, account.Balance);
            header = header.Set(PbtKeyDerivation.BasicDataLeafKey, ToLeaf(basicData));
            header = header.Set(PbtKeyDerivation.CodeHashLeafKey, ToLeaf(account.CodeHash.Bytes));

            if (chunks is null) return;

            int headerChunks = Math.Min(chunks.Length, PbtKeyDerivation.HeaderCodeChunks);
            for (int i = 0; i < headerChunks; i++)
            {
                header = header.Set(PbtKeyDerivation.HeaderCodeChunkSubIndex(i), ToLeaf(chunks[i]));
            }
        }
        finally
        {
            _dirtyStems[headerStem] = header;
        }

        // overflow chunks (index 128+) live on their own content-addressed code-zone stems
        for (int i = PbtKeyDerivation.HeaderCodeChunks; i < chunks.Length; i++)
        {
            Stem overflowStem = PbtKeyDerivation.CodeOverflowStem(account.CodeHash, i, out byte subIndex);
            SetLeaf(overflowStem, subIndex, ToLeaf(chunks[i]));
        }
    }

    /// <summary>Folds one leaf write into its stem's pooled change map.</summary>
    /// <remarks>
    /// <see cref="IPbtStemChanges.Set"/> may promote the map to a larger variant and return the old
    /// one to the pool, so its result must always be stored back; routing single-leaf writes through
    /// here makes forgetting that unrepresentable. Safe only while at most one thread writes a given
    /// stem: <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey, Func{TKey, TValue})"/> may
    /// run the factory and discard its result, which would leak the rented map. That holds because
    /// <c>PersistentStorageProvider</c> partitions its parallel batches by address, and code-overflow
    /// stems are reached only from the single-threaded account flush.
    /// </remarks>
    private void SetLeaf(in Stem stem, byte subIndex, in ValueHash256 value)
    {
        IPbtStemChanges changes = _dirtyStems.GetOrAdd(stem, static _ => PbtStemChanges.Rent());
        _dirtyStems[stem] = changes.Set(subIndex, value);
    }

    /// <summary>Drains the dirty stem leaves accumulated since the last root update into a <see cref="PbtWriteBatch"/>.</summary>
    /// <remarks>
    /// Ownership of the per-stem maps passes to the batch, which returns them to the pool on dispose, so
    /// <see cref="_dirtyStems"/> is emptied here: <see cref="UpdateRootHash"/> folds them into the overlays
    /// once and does not revisit them.
    /// </remarks>
    private PbtWriteBatch BuildChanges()
    {
        PbtWriteBatch batch = new(estimatedStems: _dirtyStems.Count);
        foreach ((Stem stem, IPbtStemChanges leaves) in _dirtyStems) batch.Add(stem, leaves);
        _dirtyStems.Clear();
        return batch;
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
        using RefCountingMemory? prior = Bundle.GetLeafBlob(headerStem);
        return prior is not null && StemLeafBlob.TryGetValue(prior.GetSpan(), PbtKeyDerivation.BasicDataLeafKey, out ReadOnlySpan<byte> basicData)
            ? PbtKeyDerivation.ReadBasicDataCodeSize(basicData)
            : 0;
    }

    private void SetSlot(Address address, in UInt256 slot, in EvmWord value)
    {
        Bundle.SetSlot(address, slot, value);
        _rootDirty = true;
    }

    // PBT does not support self-destruct; this keeps only the read-side new-account optimization,
    // where the bundle marker makes storage reads for a new or cleared account return a clean zero.
    private void SelfDestructStorage(Address address)
    {
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
            scope.Bundle.SetAccount(key, account);
            scope._rootDirty = true;

            // the world state skips the storage write batch entirely for removed accounts, so the
            // storage clear must happen here
            if (account is null) scope.SelfDestructStorage(key);
            else scope.ApplyAccountHeader(key, account);
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
            new StorageWriteBatch(scope, key);

        public void Dispose()
        {
        }
    }

    private sealed class StorageWriteBatch(PbtWorldStateScope scope, Address address) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private Stem _headerStem;
        private bool _headerStemComputed;

        public void Set(in UInt256 index, byte[] value)
        {
            // the world state passes a stripped (leading-zeros-removed) value; canonicalize to 32 bytes
            EvmWord word = EvmWordSlot.FromStripped(value);

            // route the slot to its EIP-8297 stem: the first 64 slots live in the account header,
            // the rest in their own storage-zone stem
            Stem stem;
            byte subIndex;
            if (PbtKeyDerivation.IsHeaderSlot(index))
            {
                stem = HeaderStem();
                subIndex = PbtKeyDerivation.HeaderSlotSubIndex(index);
            }
            else
            {
                stem = PbtKeyDerivation.StorageStem(address, index, out subIndex);
            }

            // single-writer per stem: this address's storage is flushed by one worker
            scope.SetLeaf(stem, subIndex, SlotLeaf(word));
            scope.SetSlot(address, index, word);
        }

        public void Clear() => scope.SelfDestructStorage(address);

        // the header stem is constant per address; derive it lazily and reuse it across header slots
        private Stem HeaderStem()
        {
            if (!_headerStemComputed)
            {
                _headerStem = PbtKeyDerivation.AccountHeaderStem(address);
                _headerStemComputed = true;
            }

            return _headerStem;
        }

        public void Dispose()
        {
        }
    }
}
