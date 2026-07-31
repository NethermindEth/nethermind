// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Flat.ScopeProvider;

namespace Nethermind.State.Pbt.ScopeProvider;

/// <summary>Provides the read/write surface for a processing branch backed by an EIP-8297 tree.</summary>
/// <remarks>
/// The scope retains the folded tree root and the header root separately. The latter is reported and
/// used to key states so Patricia-rooted blocks validate against the root their header claims.
/// </remarks>
public sealed class PbtWorldStateScope : IWorldStateScopeProvider.IScope, IPbtStore, ITrieWarmer.IAddressWarmer
{
    private static readonly byte[] _clearedAccountHeader = new byte[2 * ValueHash256.MemorySize];

    private readonly IPbtCommitTarget _commitTarget;
    private readonly IPbtChildHeaderSource _childHeaders;
    private readonly bool _isReadOnly;
    private readonly PbtTrieLayout _writeLayout;
    private readonly int _rootFoldConcurrency;
    private readonly ITrieWarmer _trieWarmer;

    // Storage and account writes use disjoint sub-index bands, allowing their batches to share this builder.
    private readonly PbtWriteBatchBuilder _writeBatchBuilder;

    // Not pooled: an unjoined code writer after a failed block could otherwise write into a later scope.
    private readonly Dictionary<ValueHash256, byte[]> _pendingCode = [];
    private readonly Dictionary<AddressAsKey, PbtStorageTree> _storages = [];
    private readonly HashSet<Stem> _queuedPrewarms = [];
    private readonly object _warmupLock = new();

    private StateId _currentStateId;
    private PbtPartitionRoots _partitionRoots;
    private Hash256 _rootHash;
    private Hash256? _authoritativeRoot;

    private BlockHeader? _currentHeader;
    private BlockHeader? _childHeader;

    private bool _rootDirty;
    private bool _isDisposed;
    private bool _pausePrewarmer;
    private int _hintSequenceId;
    private int _outstandingWarmups;

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
        PbtTrieLayout writeLayout,
        int rootFoldConcurrency,
        ITrieWarmer trieWarmer)
    {
        _currentStateId = currentStateId;
        _currentHeader = currentHeader;
        Bundle = bundle;
        _commitTarget = commitTarget;
        _childHeaders = childHeaders;
        _writeBatchBuilder = resourcePool.GetWriteBatchBuilder(usage);
        _isReadOnly = isReadOnly;
        _writeLayout = writeLayout;
        _rootFoldConcurrency = rootFoldConcurrency;
        _trieWarmer = trieWarmer;
        _partitionRoots = bundle.PartitionRoots;
        _rootHash = currentStateId.StateRoot.ToHash256();
        CodeDb = new PbtCodeDb(codeDb, _pendingCode);
        _trieWarmer.OnEnterScope();
    }

    internal PbtSnapshotBundle Bundle { get; }

    public Hash256 RootHash => _rootHash;

    /// <summary>Uses <paramref name="root"/> to report and key the current state.</summary>
    /// <remarks>
    /// The mirror scope supplies the authoritative backend's root for genesis and self-built blocks,
    /// which lack a child header. Applying it here also covers blocks with no dirty state.
    /// </remarks>
    internal void UseAuthoritativeRoot(Hash256 root)
    {
        _authoritativeRoot = root;
        _rootHash = root;
    }

    public IWorldStateScopeProvider.ICodeDb CodeDb { get; }

    public Account? Get(Address address)
    {
        Account? account = Bundle.GetAccount(address);
        HintGet(address, account);
        return account;
    }

    public void HintGet(Address address, Account? account)
    {
        Bundle.PromoteAccount(address, account);
        QueueAccountPrewarm(address);
    }

    public void HintWarmAccount(in ValueAddress address) => QueueAccountPrewarm(address.ToAddress());

    public void HintWarmSlot(in ValueAddress address, in UInt256 index) =>
        GetOrCreateStorageTree(address.ToAddress()).QueuePrewarm(index, multiProducer: true);

    public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) => Task.CompletedTask;

    public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => GetOrCreateStorageTree(address);

    private PbtStorageTree GetOrCreateStorageTree(Address address)
    {
        lock (_storages)
        {
            ref PbtStorageTree? tree = ref CollectionsMarshal.GetValueRefOrAddDefault(_storages, address, out bool exists);
            if (!exists) tree = new PbtStorageTree(this, address, _trieWarmer);
            return tree!;
        }
    }

    private void QueueAccountPrewarm(Address address)
    {
        Stem stem = PbtKeyDerivation.AccountHeaderStem(address);
        if (!TryReservePrewarm(stem, out int sequenceId)) return;

        if (_trieWarmer.PushAddressJob(this, address, sequenceId))
            Metrics.IncrementPbtTrieWarmerTriggered();
        else
            CancelPrewarm(stem);
    }

    internal bool TryReservePrewarm(in Stem stem, out int sequenceId)
    {
        lock (_warmupLock)
        {
            sequenceId = _hintSequenceId;
            if (_isDisposed || _pausePrewarmer) return false;
            if (!_queuedPrewarms.Add(stem))
            {
                Metrics.IncrementPbtTrieWarmerSkippedByDeduplication();
                return false;
            }

            _outstandingWarmups++;
            return true;
        }
    }

    internal void CancelPrewarm(in Stem stem)
    {
        lock (_warmupLock)
        {
            _queuedPrewarms.Remove(stem);
            CompletePrewarmUnderLock();
        }
    }

    internal int HintSequenceId => Volatile.Read(ref _hintSequenceId);

    internal PbtTrieLayout WriteLayout => _writeLayout;

    internal PbtPartitionRoots PartitionRoots => _partitionRoots;

    internal bool IsDisposed => Volatile.Read(ref _isDisposed);

    public bool WarmUpStateTrie(Address address, int sequenceId)
    {
        try
        {
            if (IsDisposed || HintSequenceId != sequenceId) return false;
            Stem stem = PbtKeyDerivation.AccountHeaderStem(address);
            return Bundle.WarmStemPath(_writeLayout, _partitionRoots, stem);
        }
        finally
        {
            CompletePrewarm();
        }
    }

    public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => new WriteBatch(this);

    /// <summary>Folds dirty stems into the tree and records the resulting nodes and blobs in the write buffer.</summary>
    /// <remarks>
    /// A block header root takes precedence over the folded root. Synthetic and self-built blocks have
    /// no child header, so they use the folded root they carry when later processed.
    /// </remarks>
    public void UpdateRootHash()
    {
        if (!_rootDirty) return;

        PauseAndDrainPrewarmer();
        try
        {
            UpdateRootHashCore();
        }
        finally
        {
            ResumePrewarmer();
        }
    }

    private void UpdateRootHashCore()
    {
        if (!_rootDirty) return;

        long start = Stopwatch.GetTimestamp();
        using (PbtWriteBatchSet changes = _writeBatchBuilder.DrainToWriteBatches())
        {
            _partitionRoots = TrieUpdater.UpdateRoot(
                this, _partitionRoots, changes, PooledRefCountingMemoryProvider.Instance, _writeLayout, _rootFoldConcurrency, out _);
        }
        Metrics.PbtRootHashTime.Observe(Stopwatch.GetTimestamp() - start);

        _childHeader ??= _currentHeader is null ? null : _childHeaders.TryFindChild(_currentHeader);
        _rootHash = _authoritativeRoot ?? _childHeader?.StateRoot ?? _partitionRoots.Root.ToHash256();
        _rootDirty = false;
    }

    RefCountingMemory? IPbtStore.GetTrieNode(in TrieNodeKey key, in ValueHash256 hash) => Bundle.GetTrieNode(key, hash);

    RefCountingMemory? IPbtStore.GetLeafBlob(in Stem stem, in ValueHash256 hash) => Bundle.GetLeafBlob(stem, hash);

    void IPbtStore.SetTrieNode(in TrieNodeKey key, in ValueHash256 hash, RefCountingMemory? node) => Bundle.SetOwnedTrieNode(key, hash, node);

    void IPbtStore.SetLeafBlob(in Stem stem, in ValueHash256 hash, RefCountingMemory? blob) => Bundle.SetOwnedLeafBlob(stem, hash, blob);

    public void Commit(ulong blockNumber)
    {
        PauseAndDrainPrewarmer();
        try
        {
            UpdateRootHashCore();

            StateId newStateId = new(blockNumber, _rootHash);
            if (newStateId != _currentStateId)
            {
                PbtSnapshot snapshot = Bundle.CollectSnapshot(_currentStateId, newStateId, _partitionRoots);
                if (_isReadOnly)
                {
                    snapshot.Dispose();
                }
                else
                {
                    _commitTarget.AddSnapshot(snapshot);
                }

                _currentStateId = newStateId;
            }

            // Clear the header when no child was found so the next block does not resolve itself as its child.
            _currentHeader = _childHeader;
            _childHeader = null;

            _pendingCode.Clear();
            lock (_storages) _storages.Clear();
            _rootDirty = false;
        }
        finally
        {
            ResumePrewarmer();
        }
    }

    /// <summary>Stops and joins warmers before the fold mutates or snapshot collection resets the mutable bundle.</summary>
    private void PauseAndDrainPrewarmer()
    {
        lock (_warmupLock)
        {
            _pausePrewarmer = true;
            _hintSequenceId++;
            while (_outstandingWarmups != 0) Monitor.Wait(_warmupLock);
            _queuedPrewarms.Clear();
        }
    }

    private void ResumePrewarmer()
    {
        lock (_warmupLock) _pausePrewarmer = false;
    }

    internal void CompletePrewarm()
    {
        lock (_warmupLock) CompletePrewarmUnderLock();
    }

    private void CompletePrewarmUnderLock()
    {
        if (--_outstandingWarmups == 0) Monitor.PulseAll(_warmupLock);
    }

    /// <remarks>Disposing returns pending stem-change maps to the pool when a scope is abandoned before its final fold.</remarks>
    public void Dispose()
    {
        lock (_warmupLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _pausePrewarmer = true;
            _hintSequenceId++;
            while (_outstandingWarmups != 0) Monitor.Wait(_warmupLock);
        }

        try
        {
            _writeBatchBuilder.Dispose();
        }
        finally
        {
            try
            {
                Bundle.Dispose();
            }
            finally
            {
                _trieWarmer.OnExitScope();
            }
        }
    }

    private void ApplyAccountHeader(Address address, Account account)
    {
        Stem headerStem = PbtKeyDerivation.AccountHeaderStem(address);

        // Unchanged code is not in the pending map, so recover its size without fetching its bytes.
        byte[]? updatedCode = account.HasCode && _pendingCode.TryGetValue(account.CodeHash, out byte[]? c) ? c : null;
        uint codeSize = updatedCode is not null ? (uint)updatedCode.Length
            : !account.HasCode ? 0
            : PriorCodeSize(headerStem);
        byte[]? chunks = updatedCode is null ? null : PbtKeyDerivation.ChunkifyCode(updatedCode);

        Span<byte> basicDataAndCodeHash = stackalloc byte[2 * ValueHash256.MemorySize];
        PbtKeyDerivation.PackBasicData(basicDataAndCodeHash[..ValueHash256.MemorySize], codeSize, account.Nonce, account.Balance);
        account.CodeHash.Bytes.CopyTo(basicDataAndCodeHash[ValueHash256.MemorySize..]);
        _writeBatchBuilder.SetLeafRange(headerStem, PbtKeyDerivation.BasicDataLeafKey, basicDataAndCodeHash);

        if (chunks is null) return;

        int chunkCount = chunks.Length / PbtKeyDerivation.CodeChunkSize;
        int headerChunks = Math.Min(chunkCount, PbtKeyDerivation.HeaderCodeChunks);
        _writeBatchBuilder.SetLeafRange(headerStem, PbtKeyDerivation.HeaderCodeChunkSubIndex(0), ChunkRun(chunks, 0, headerChunks));

        for (int i = PbtKeyDerivation.HeaderCodeChunks; i < chunkCount;)
        {
            Stem overflowStem = PbtKeyDerivation.CodeOverflowStem(account.CodeHash, i, out byte subIndex);
            int run = Math.Min(chunkCount - i, PbtKeyDerivation.StemSubtreeWidth - subIndex);
            _writeBatchBuilder.SetLeafRange(overflowStem, subIndex, ChunkRun(chunks, i, run));
            i += run;
        }
    }

    // Clearing BASIC_DATA and CODE_HASH makes the account absent; clearing unused header leaves is unnecessary.
    private void ClearAccountHeader(Address address) =>
        _writeBatchBuilder.SetLeafRange(PbtKeyDerivation.AccountHeaderStem(address), PbtKeyDerivation.BasicDataLeafKey, _clearedAccountHeader);

    private static ReadOnlySpan<byte> ChunkRun(byte[] chunks, int firstChunk, int count) =>
        chunks.AsSpan(firstChunk * PbtKeyDerivation.CodeChunkSize, count * PbtKeyDerivation.CodeChunkSize);

    private static ValueHash256 SlotLeaf(in EvmWord value) =>
        EvmWordSlot.IsZero(value) ? default : new ValueHash256(EvmWordSlot.AsReadOnlySpan(in value));

    private uint PriorCodeSize(in Stem headerStem)
    {
        using RefCountingMemory? prior = Bundle.GetLeafBlob(headerStem);
        return prior is not null && StemLeafBlob.TryGetValue(prior.GetSpan(), PbtKeyDerivation.BasicDataLeafKey, out ReadOnlySpan<byte> basicData)
            ? PbtKeyDerivation.ReadBasicDataCodeSize(basicData)
            : 0;
    }

    // The marker lets cleared-account storage reads return zero without rewriting all storage stems.
    private void SelfDestructStorage(Address address)
    {
        Bundle.SelfDestruct(address);
        _rootDirty = true;
    }

    private sealed class WriteBatch(PbtWorldStateScope scope) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly long _start = Stopwatch.GetTimestamp();

        // PBT accounts have no storage root to propagate.
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

            if (account is null)
            {
                // Removed accounts bypass the storage batch, so clear their storage here.
                scope.ClearAccountHeader(key);
                scope.SelfDestructStorage(key);
            }
            else
            {
                scope.ApplyAccountHeader(key, account);
            }
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
            new StorageWriteBatch(scope, key);

        public void Dispose() => Metrics.PbtWriteBatchTime.Observe(Stopwatch.GetTimestamp() - _start);
    }

    private sealed class StorageWriteBatch(PbtWorldStateScope scope, Address address) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private PbtSlotKeyDeriver _deriver = new(address);

        public void Set(in UInt256 index, byte[] value)
        {
            EvmWord word = EvmWordSlot.FromStripped(value);

            Stem stem = _deriver.Derive(index, out byte subIndex);

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
