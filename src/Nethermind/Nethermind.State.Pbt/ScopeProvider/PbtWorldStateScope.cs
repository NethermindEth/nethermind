// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Flat.ScopeProvider;

namespace Nethermind.State.Pbt.ScopeProvider;

/// <summary>Provides the read/write surface for a processing branch backed by one canonical EIP-8297 tree.</summary>
public sealed class PbtWorldStateScope : IWorldStateScopeProvider.IScope, ITrieWarmer.IAddressWarmer
{
    private readonly IPbtCommitTarget _commitTarget;
    private readonly IPbtChildHeaderSource _childHeaders;
    private readonly bool _isReadOnly;
    private readonly ITrieWarmer _trieWarmer;
    private readonly Dictionary<ValueHash256, byte[]> _pendingCode = [];
    private readonly Dictionary<AddressAsKey, PbtStorageTree> _storages = [];
    private readonly HashSet<Stem> _queuedPrewarms = [];
    private readonly object _warmupLock = new();

    private StateId _currentStateId;
    private Hash256 _rootHash;
    private ValueHash256 _treeRoot;
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
        _isReadOnly = isReadOnly;
        _trieWarmer = trieWarmer;
        _treeRoot = bundle.TreeRoot;
        _rootHash = currentStateId.StateRoot.ToHash256();
        CodeDb = new PbtCodeDb(codeDb, _pendingCode);
        _trieWarmer.OnEnterScope();
    }

    internal PbtSnapshotBundle Bundle { get; }
    public Hash256 RootHash => _rootHash;
    public IWorldStateScopeProvider.ICodeDb CodeDb { get; }
    internal bool IsDisposed => Volatile.Read(ref _isDisposed);
    internal int HintSequenceId => Volatile.Read(ref _hintSequenceId);

    internal void UseAuthoritativeRoot(Hash256 root)
    {
        _authoritativeRoot = root;
        _rootHash = root;
    }

    public Account? Get(Address address)
    {
        Account? account = Bundle.GetAccount(address);
        HintGet(address, account);
        return account;
    }

    public void HintGet(Address address, Account? account) => Bundle.PromoteAccount(address, account);
    public void HintWarmAccount(in ValueAddress address) { }
    public void HintWarmSlot(in ValueAddress address, in UInt256 index) { }
    public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) => Task.CompletedTask;

    public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => GetOrCreateStorageTree(address);

    private PbtStorageTree GetOrCreateStorageTree(Address address)
    {
        lock (_storages)
        {
            ref PbtStorageTree? tree = ref CollectionsMarshal.GetValueRefOrAddDefault(_storages, address, out bool exists);
            if (!exists) tree = new PbtStorageTree(this, address);
            return tree!;
        }
    }

    internal bool TryReservePrewarm(in Stem stem, out int sequenceId)
    {
        lock (_warmupLock)
        {
            sequenceId = _hintSequenceId;
            if (_isDisposed || _pausePrewarmer) return false;
            if (!_queuedPrewarms.Add(stem)) return false;
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

    internal void CompletePrewarm()
    {
        lock (_warmupLock) CompletePrewarmUnderLock();
    }

    private void CompletePrewarmUnderLock()
    {
        if (--_outstandingWarmups == 0) Monitor.PulseAll(_warmupLock);
    }

    public bool WarmUpStateTrie(Address address, int sequenceId)
    {
        CompletePrewarm();
        return false;
    }

    public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => new WriteBatch(this);

    public void UpdateRootHash()
    {
        if (!_rootDirty) return;
        long start = Stopwatch.GetTimestamp();
        PbtCanonicalBuildResult result = PbtCanonicalTree.RebuildWithNodes(Bundle.EnumerateLeaves());
        Bundle.ReplaceNodes(result.Nodes);
        _treeRoot = result.RootHash;
        Metrics.PbtRootHashTime.Observe(Stopwatch.GetTimestamp() - start);
        _childHeader ??= _currentHeader is null ? null : _childHeaders.TryFindChild(_currentHeader);
        _rootHash = _authoritativeRoot ?? _childHeader?.StateRoot ?? _treeRoot.ToHash256();
        _rootDirty = false;
    }

    public void Commit(ulong blockNumber)
    {
        PauseAndDrainPrewarmer();
        try
        {
            UpdateRootHash();
            StateId newStateId = new(blockNumber, _rootHash);
            if (newStateId != _currentStateId)
            {
                PbtSnapshot snapshot = Bundle.CollectSnapshot(_currentStateId, newStateId, _treeRoot);
                if (_isReadOnly) snapshot.Dispose();
                else _commitTarget.AddSnapshot(snapshot);
                _currentStateId = newStateId;
            }
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
            Bundle.Dispose();
        }
        finally
        {
            _trieWarmer.OnExitScope();
        }
    }

    private void ApplyAccount(Address address, Account account)
    {
        ValueHash256 oldCodeHash = Bundle.GetAccount(address)?.CodeHash.ValueHash256 ?? ValueKeccak.OfAnEmptyString;
        ValueHash256 newCodeHash = account.CodeHash.ValueHash256;
        if (oldCodeHash != newCodeHash)
        {
            RemoveCodeReference(oldCodeHash);
            AddCodeReference(newCodeHash);
        }

        byte[]? code = account.HasCode && _pendingCode.TryGetValue(newCodeHash, out byte[]? pending) ? pending : null;
        ValueHash256? prior = Bundle.GetLeaf(PbtStateKey.Account(address, PbtKeyDerivation.BasicDataLeafKey));
        uint codeSize = code is not null ? (uint)code.Length
            : !account.HasCode ? 0
            : prior is null ? 0 : PbtKeyDerivation.ReadBasicDataCodeSize(prior.Value.Bytes);
        Span<byte> basicData = stackalloc byte[ValueHash256.MemorySize];
        PbtKeyDerivation.PackBasicData(basicData, codeSize, account.Nonce, account.Balance);
        Bundle.SetLeaf(PbtStateKey.Account(address, PbtKeyDerivation.BasicDataLeafKey), new ValueHash256(basicData));
        Bundle.SetLeaf(PbtStateKey.Account(address, PbtKeyDerivation.CodeHashLeafKey), newCodeHash);

        if (code is null) return;
        byte[] chunks = PbtKeyDerivation.ChunkifyCode(code);
        int count = chunks.Length / PbtKeyDerivation.CodeChunkSize;
        for (int chunkId = 0; chunkId < count; chunkId++)
        {
            ReadOnlySpan<byte> chunk = chunks.AsSpan(chunkId * PbtKeyDerivation.CodeChunkSize, PbtKeyDerivation.CodeChunkSize);
            PbtFullKey key = PbtStateKey.Code(address, newCodeHash, chunkId);
            Bundle.SetLeaf(key, chunk.IndexOfAnyExcept((byte)0) < 0 ? null : new ValueHash256(chunk));
        }
    }

    private void DeleteAccount(Address address)
    {
        Account? prior = Bundle.GetAccount(address);
        if (prior is not null) RemoveCodeReference(prior.CodeHash.ValueHash256);
        Bundle.DeletePrefix(PbtStateKey.AccountPrefix(address));
        Bundle.DeletePrefix(PbtStateKey.StoragePrefix(address));
        Bundle.SelfDestruct(address);
    }

    private void AddCodeReference(in ValueHash256 codeHash)
    {
        if (codeHash == ValueKeccak.OfAnEmptyString) return;
        Bundle.SetCodeReference(codeHash, checked(Bundle.GetCodeReference(codeHash) + 1));
    }

    private void RemoveCodeReference(in ValueHash256 codeHash)
    {
        if (codeHash == ValueKeccak.OfAnEmptyString) return;
        ulong count = Bundle.GetCodeReference(codeHash);
        if (count <= 1)
        {
            Bundle.SetCodeReference(codeHash, null);
            Bundle.DeletePrefix(PbtStateKey.OverflowCodePrefix(codeHash));
        }
        else
        {
            Bundle.SetCodeReference(codeHash, count - 1);
        }
    }

    private sealed class WriteBatch(PbtWorldStateScope scope) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated { add { } remove { } }

        public void Set(Address key, Account? account)
        {
            scope.Bundle.SetAccount(key, account);
            if (account is null) scope.DeleteAccount(key);
            else scope.ApplyAccount(key, account.WithChangedStorageRoot(Keccak.EmptyTreeHash));
            scope._rootDirty = true;
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
            new StorageWriteBatch(scope, key);

        public void Dispose() => Metrics.PbtWriteBatchTime.Observe(Stopwatch.GetTimestamp() - _start);
    }

    private sealed class StorageWriteBatch(PbtWorldStateScope scope, Address address) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Set(in UInt256 index, byte[] value)
        {
            EvmWord word = EvmWordSlot.FromStripped(value);
            scope.Bundle.SetLeaf(PbtStateKey.Storage(address, index),
                EvmWordSlot.IsZero(word) ? null : new ValueHash256(EvmWordSlot.AsReadOnlySpan(in word)));
            scope.Bundle.SetSlot(address, index, word);
            scope._rootDirty = true;
        }

        public void Clear()
        {
            scope.Bundle.SelfDestruct(address);
            scope._rootDirty = true;
        }

        public void Dispose() { }
    }
}
