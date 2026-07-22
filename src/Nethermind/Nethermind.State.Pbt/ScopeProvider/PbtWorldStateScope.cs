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
/// <remarks>
/// The fold's own root is not what <see cref="RootHash"/> reports: on a Patricia-rooted chain the
/// block being processed already claims a root, and reporting anything else fails validation. The
/// scope therefore tracks both — the EIP-8297 root the tree folds to, which the next fold and the
/// sealed snapshot need, and the header's root, which is what it reports and keys its states by. See
/// <see cref="IPbtChildHeaderSource"/>.
/// </remarks>
public sealed class PbtWorldStateScope : IWorldStateScopeProvider.IScope, IPbtStore
{
    private readonly IPbtCommitTarget _commitTarget;
    private readonly IPbtChildHeaderSource _childHeaders;
    private readonly bool _isReadOnly;
    private readonly PbtGroupFormat _writeFormat;
    private readonly int _rootFoldConcurrency;

    // stem leaves dirtied since the last root update: storage slots from the parallel storage batches
    // and account/code header leaves from the single-threaded account flush both land here (their
    // sub-index bands never overlap). UpdateRootHash drains it into the write batch.
    private readonly PbtWriteBatchBuilder _writeBatchBuilder;

    // deliberately not pooled: PbtCodeDb captures this by reference and StateProvider.CommitCodeAsync
    // ends in a Task.Run joined only on the success paths, so a writer orphaned by a failed block
    // would bleed code into whichever block rented the map next
    private readonly Dictionary<ValueHash256, byte[]> _pendingCode = [];
    private readonly Dictionary<AddressAsKey, PbtStorageTree> _storages = [];

    private StateId _currentStateId;
    private ValueHash256 _treeRoot;
    private Hash256 _rootHash;

    // the header of the state the scope currently sits on, and the header of the block it is folding;
    // the latter is resolved once per block and carried into Commit so a branch's next block starts
    // from the header it just committed
    private BlockHeader? _currentHeader;
    private BlockHeader? _childHeader;

    private bool _rootDirty;
    private bool _isDisposed;

    public PbtWorldStateScope(
        in StateId currentStateId,
        BlockHeader? currentHeader,
        PbtSnapshotBundle bundle,
        IWorldStateScopeProvider.ICodeDb codeDb,
        IPbtCommitTarget commitTarget,
        IPbtChildHeaderSource childHeaders,
        IPbtResourcePool resourcePool,
        PbtResourcePool.Usage usage,
        bool isReadOnly,
        PbtGroupFormat writeFormat,
        int rootFoldConcurrency)
    {
        _currentStateId = currentStateId;
        _currentHeader = currentHeader;
        Bundle = bundle;
        _commitTarget = commitTarget;
        _childHeaders = childHeaders;
        _writeBatchBuilder = resourcePool.GetWriteBatchBuilder(usage);
        _isReadOnly = isReadOnly;
        _writeFormat = writeFormat;
        _rootFoldConcurrency = rootFoldConcurrency;
        _treeRoot = bundle.TreeRoot;
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
    /// recording the new blobs and nodes in the bundle's write buffer and flushing the dirty set.
    /// </summary>
    /// <remarks>
    /// The reported root is the one the block's own header claims. Falling back to the tree's root
    /// when no such header exists keeps the blocks this node builds itself — and every scope over a
    /// synthetic block — self-consistent: such a block carries the tree root, and echoes it back when
    /// it is later processed.
    /// </remarks>
    public void UpdateRootHash()
    {
        if (!_rootDirty) return;

        using PbtWriteBatch changes = _writeBatchBuilder.DrainToWriteBatch();
        _treeRoot = TrieUpdater.UpdateRoot(
            this, _treeRoot, changes, PooledRefCountingMemoryProvider.Instance, _writeFormat, _rootFoldConcurrency, out _);
        _childHeader ??= _currentHeader is null ? null : _childHeaders.TryFindChild(_currentHeader);
        _rootHash = _childHeader?.StateRoot ?? _treeRoot.ToHash256();
        _rootDirty = false;
    }

    RefCountingMemory? IPbtStore.GetTrieNode(in TrieNodeKey key) => Bundle.GetTrieNode(key);

    RefCountingMemory? IPbtStore.GetLeafBlob(in Stem stem) => Bundle.GetLeafBlob(stem);

    void IPbtStore.SetTrieNode(in TrieNodeKey key, byte[]? node) => Bundle.SetTrieNode(key, node);

    // an emptied stem is buffered as an empty blob: the tombstone that shadows the layer chain's blob
    void IPbtStore.SetLeafBlob(in Stem stem, byte[]? blob) => Bundle.SetLeafBlob(stem, blob ?? []);

    public void Commit(ulong blockNumber)
    {
        UpdateRootHash();

        StateId newStateId = new(blockNumber, _rootHash);
        if (newStateId != _currentStateId)
        {
            PbtSnapshot snapshot = Bundle.CollectSnapshot(_currentStateId, newStateId, _treeRoot);
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

        // unconditionally, null included: keeping the old header would have the next block in the
        // branch resolve the child of the block this one just committed — the block itself
        _currentHeader = _childHeader;
        _childHeader = null;

        // the dirty stems were already drained (and their maps returned) by UpdateRootHash, and the
        // blob and node results left with the buffer CollectSnapshot sealed
        _pendingCode.Clear();
        _storages.Clear();
        _rootDirty = false;
    }

    /// <remarks>
    /// Returning the builder hands back the stem-change maps no fold claimed: a scope abandoned with
    /// pending writes — an exception mid-block, or a branch dropped before its final
    /// <see cref="UpdateRootHash"/> — would otherwise lose them to the GC rather than the pool.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;

        try
        {
            _writeBatchBuilder.Dispose();
        }
        finally
        {
            Bundle.Dispose();
        }
    }

    /// <summary>
    /// Folds an account's EIP-8297 header leaves straight into the dirty stems: BASIC_DATA,
    /// CODE_HASH and the header code chunks on its header stem, plus any overflow chunks on their
    /// own stems.
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
        byte[]? chunks = updatedCode is null ? null : PbtKeyDerivation.ChunkifyCode(updatedCode);

        // BASIC_DATA and CODE_HASH sit on adjacent sub-indices, so they go in as one run
        Span<byte> basicDataAndCodeHash = stackalloc byte[2 * ValueHash256.MemorySize];
        PbtKeyDerivation.PackBasicData(basicDataAndCodeHash[..ValueHash256.MemorySize], codeSize, account.Nonce, account.Balance);
        account.CodeHash.Bytes.CopyTo(basicDataAndCodeHash[ValueHash256.MemorySize..]);
        _writeBatchBuilder.SetLeafRange(headerStem, PbtKeyDerivation.BasicDataLeafKey, basicDataAndCodeHash);

        if (chunks is null) return;

        int chunkCount = chunks.Length / PbtKeyDerivation.CodeChunkSize;
        int headerChunks = Math.Min(chunkCount, PbtKeyDerivation.HeaderCodeChunks);
        _writeBatchBuilder.SetLeafRange(headerStem, PbtKeyDerivation.HeaderCodeChunkSubIndex(0), ChunkRun(chunks, 0, headerChunks));

        // overflow chunks (index 128+) live on their own content-addressed code-zone stems, each
        // holding a run of up to a full stem's worth before the next stem takes over
        for (int i = PbtKeyDerivation.HeaderCodeChunks; i < chunkCount;)
        {
            Stem overflowStem = PbtKeyDerivation.CodeOverflowStem(account.CodeHash, i, out byte subIndex);
            int run = Math.Min(chunkCount - i, PbtKeyDerivation.StemSubtreeWidth - subIndex);
            _writeBatchBuilder.SetLeafRange(overflowStem, subIndex, ChunkRun(chunks, i, run));
            i += run;
        }
    }

    private static ReadOnlySpan<byte> ChunkRun(byte[] chunks, int firstChunk, int count) =>
        chunks.AsSpan(firstChunk * PbtKeyDerivation.CodeChunkSize, count * PbtKeyDerivation.CodeChunkSize);

    private static ValueHash256 SlotLeaf(in EvmWord value) =>
        EvmWordSlot.IsZero(value) ? default : new ValueHash256(EvmWordSlot.AsReadOnlySpan(in value));

    /// <summary>Reads the code size from the account's prior <c>BASIC_DATA</c> leaf; 0 if the account is new.</summary>
    private uint PriorCodeSize(in Stem headerStem)
    {
        using RefCountingMemory? prior = Bundle.GetLeafBlob(headerStem);
        return prior is not null && StemLeafBlob.TryGetValue(prior.GetSpan(), PbtKeyDerivation.BasicDataLeafKey, out ReadOnlySpan<byte> basicData)
            ? PbtKeyDerivation.ReadBasicDataCodeSize(basicData)
            : 0;
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
        private PbtSlotKeyDeriver _deriver = new(address);

        public void Set(in UInt256 index, byte[] value)
        {
            EvmWord word = EvmWordSlot.FromStripped(value);

            Stem stem = _deriver.Derive(index, out byte subIndex);

            // single-writer per stem: this address's storage is flushed by one worker
            scope._writeBatchBuilder.SetLeaf(stem, subIndex, SlotLeaf(word));
            scope.Bundle.SetSlot(address, index, word);
            scope._rootDirty = true;
        }

        public void Clear() => scope.SelfDestructStorage(address);

        public void Dispose()
        {
        }
    }
}
