// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
/// <see cref="IPbtStore"/> the updater reads/writes: both go through the bundle's write buffer, which
/// shadows the layer chain, so a fold composes on top of any earlier fold this block and
/// <see cref="Commit"/> seals the accumulated buffer into the snapshot.
/// </summary>
public sealed class PbtWorldStateScope : IWorldStateScopeProvider.IScope, IPbtStore
{
    private readonly IPbtCommitTarget _commitTarget;
    private readonly IPbtResourcePool _resourcePool;
    private readonly PbtResourcePool.Usage _usage;
    private readonly bool _isReadOnly;

    // stem leaves dirtied since the last root update: storage slots from the parallel storage batches
    // and account/code header leaves from the single-threaded account flush both land here (their
    // sub-index bands never overlap). UpdateRootHash drains it into the write batch.
    private readonly PbtTransientResource _transient;

    // deliberately not pooled: PbtCodeDb captures this by reference and StateProvider.CommitCodeAsync
    // ends in a Task.Run joined only on the success paths, so a writer orphaned by a failed block
    // would bleed code into whichever block rented the map next
    private readonly Dictionary<ValueHash256, byte[]> _pendingCode = [];
    private readonly Dictionary<AddressAsKey, PbtStorageTree> _storages = [];

    private StateId _currentStateId;
    private Hash256 _rootHash;
    private bool _rootDirty;
    private bool _isDisposed;

    public PbtWorldStateScope(
        in StateId currentStateId,
        PbtSnapshotBundle bundle,
        IWorldStateScopeProvider.ICodeDb codeDb,
        IPbtCommitTarget commitTarget,
        IPbtResourcePool resourcePool,
        PbtResourcePool.Usage usage,
        bool isReadOnly)
    {
        _currentStateId = currentStateId;
        Bundle = bundle;
        _commitTarget = commitTarget;
        _resourcePool = resourcePool;
        _usage = usage;
        _transient = resourcePool.GetTransientResource(usage);
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

        using PbtWriteBatch changes = _transient.DrainToWriteBatch();
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

        // the dirty stems were already drained (and their maps returned) by UpdateRootHash, and the
        // blob and node results left with the buffer CollectSnapshot sealed
        _pendingCode.Clear();
        _storages.Clear();
        _rootDirty = false;
    }

    /// <remarks>
    /// Returns the transient, whose reset hands back the stem-change maps no fold claimed: a scope
    /// abandoned with pending writes — an exception mid-block, or a branch dropped before its final
    /// <see cref="UpdateRootHash"/> — would otherwise lose them to the GC rather than the pool.
    /// Idempotent, so a double dispose cannot return anything twice.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;

        try
        {
            // reset here rather than leaning on the pool doing it: the drain is the fix, not an
            // incidental property of how the pool happens to recycle
            _transient.Reset();
        }
        finally
        {
            _resourcePool.ReturnTransientResource(_usage, _transient);
            Bundle.Dispose();
        }
    }

    /// <summary>
    /// Folds an account's EIP-8297 header leaves straight into the dirty stems: BASIC_DATA,
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
        IPbtStemChanges header = _transient.RentStemChanges(headerStem);
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
            _transient.StoreStemChanges(headerStem, header);
        }

        // overflow chunks (index 128+) live on their own content-addressed code-zone stems
        for (int i = PbtKeyDerivation.HeaderCodeChunks; i < chunks.Length; i++)
        {
            Stem overflowStem = PbtKeyDerivation.CodeOverflowStem(account.CodeHash, i, out byte subIndex);
            _transient.SetLeaf(overflowStem, subIndex, ToLeaf(chunks[i]));
        }
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
            scope._transient.SetLeaf(stem, subIndex, SlotLeaf(word));
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
