// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat.ScopeProvider;

public sealed class FlatWorldStateScope : IWorldStateScopeProvider.IScope, ITrieWarmer.IAddressWarmer
{
    private readonly SnapshotBundle _snapshotBundle;
    private readonly IFlatCommitTarget _commitTarget;
    private readonly IFlatDbConfig _configuration;
    private readonly ITrieWarmer _warmer;
    private readonly ILogManager _logManager;
    private readonly bool _isReadOnly;

    private readonly ConcurrencyController _concurrencyQuota;
    private readonly PatriciaTree _warmupStateTree;
    private readonly StateTree _stateTree;
    private readonly Dictionary<AddressAsKey, FlatStorageTree> _storages = new();
    private bool _isDisposed = false;

    // The sequence id is for stopping trie warmer for doing work while committing. Incrementing this value invalidates
    // tasks within the trie warmer's ring buffer.
    private int _hintSequenceId = 0;
    private StateId _currentStateId;
    internal bool _pausePrewarmer = false;

    public FlatWorldStateScope(
        StateId currentStateId,
        SnapshotBundle snapshotBundle,
        IWorldStateScopeProvider.ICodeDb codeDb,
        IFlatCommitTarget commitTarget,
        IFlatDbConfig configuration,
        ITrieWarmer trieCacheWarmer,
        ILogManager logManager,
        bool isReadOnly = false)
    {
        _currentStateId = currentStateId;
        _snapshotBundle = snapshotBundle;
        CodeDb = codeDb;
        _commitTarget = commitTarget;

        _concurrencyQuota = new ConcurrencyController(Environment.ProcessorCount); // Used during tree commit.
        _stateTree = new(
            new StateTrieStoreAdapter(snapshotBundle, _concurrencyQuota),
            logManager
        )
        {
            RootHash = currentStateId.StateRoot.ToCommitment()
        };

        _warmupStateTree = new(
            new StateTrieStoreWarmerAdapter(snapshotBundle),
            logManager
        )
        {
            RootHash = currentStateId.StateRoot.ToCommitment()
        };

        _configuration = configuration;
        _logManager = logManager;
        _warmer = trieCacheWarmer;

        _warmer.OnEnterScope();
        _isReadOnly = isReadOnly;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;
        _snapshotBundle.Dispose();
        _warmer.OnExitScope();
    }

    public Hash256 RootHash => _stateTree.RootHash;
    public void UpdateRootHash() => _stateTree.UpdateRootHash();

    public Account? Get(Address address)
    {
        Account? account = _snapshotBundle.GetAccount(address);

        HintGet(address, account);

        if (_configuration.VerifyWithTrie)
        {
            Account? accTrie = _stateTree.Get(address);
            if (accTrie != account)
            {
                throw new TrieException($"Incorrect account {address}, account hash {address.ToAccountPath}, trie: {accTrie} vs flat: {account}");
            }
        }

        return account;
    }

    public void HintGet(Address address, Account? account)
    {
        _snapshotBundle.SetAccount(address, account);
        if (_snapshotBundle.ShouldQueuePrewarm(address))
        {
            _warmer.PushAddressJob(this, address, _hintSequenceId);
        }
    }

    public void HintBal(BlockAccessList bal) { }

    public Task ReadBalAsync(BlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink sink, CancellationToken cancellationToken)
    {
        // Phase 1: Bulk read all accounts from the state trie
        int accountCount = 0;
        foreach (AccountChanges _ in bal.AccountChanges)
            accountCount++;

        if (accountCount == 0)
            return Task.CompletedTask;

        AccountChanges[] accountChangesList = new AccountChanges[accountCount];
        int idx = 0;
        foreach (AccountChanges ac in bal.AccountChanges)
        {
            accountChangesList[idx] = ac;
            idx++;
        }

        // Precompute keccak hashes and radix sort for disk cache locality
        int[] sortedOrder = RadixSortAddresses(accountChangesList, accountCount);

        Account?[] accounts = new Account?[accountCount];
        Parallel.For(0, accountCount, (i) =>
        {
            int orig = sortedOrder[i];
            Address address = accountChangesList[orig].Address;
            Account? account = _snapshotBundle.GetAccount(address);
            accounts[orig] = account;
            sink.OnAccountRead(address, account);
        });

        // HintGet sequentially — triggers warmer jobs and snapshot cache updates
        for (int i = 0; i < accountCount; i++)
            HintGet(accountChangesList[i].Address, accounts[i]);

        cancellationToken.ThrowIfCancellationRequested();

        // Phase 2: Per-account bulk read of storage slots
        for (int i = 0; i < accountCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AccountChanges accountChanges = accountChangesList[i];
            Account? account = accounts[i];

            int slotCount = accountChanges.StorageChanges.Count + accountChanges.StorageReads.Count;
            if (slotCount == 0 || account is null)
                continue;

            Hash256 storageRoot = account.StorageRoot ?? Keccak.EmptyTreeHash;
            if (storageRoot == Keccak.EmptyTreeHash)
                continue;

            Address address = accountChanges.Address;
            IWorldStateScopeProvider.IStorageTree storageTree = CreateStorageTree(address);

            UInt256[] slotIndices = new UInt256[slotCount];
            int si = 0;

            foreach (SlotChanges slotChanges in accountChanges.StorageChanges)
                slotIndices[si++] = slotChanges.Slot;

            foreach (StorageRead storageRead in accountChanges.StorageReads)
                slotIndices[si++] = storageRead.Key;

            // Radix sort storage slot hashes for disk cache locality
            int[] slotOrder = RadixSortStorageSlots(slotIndices, slotCount);

            byte[][] slotValues = new byte[slotCount][];
            Parallel.For(0, slotCount, (si2) =>
            {
                int orig = slotOrder[si2];
                byte[] value = storageTree.Get(in slotIndices[orig]);
                slotValues[orig] = value;
                StorageCell cell = new(address, slotIndices[orig]);
                sink.OnStorageRead(in cell, value);
            });

            // HintGet sequentially — triggers warmer jobs
            for (int si2 = 0; si2 < slotCount; si2++)
                storageTree.HintGet(in slotIndices[si2], slotValues[si2]);
        }

        return Task.CompletedTask;
    }

    private static int[] RadixSortAddresses(AccountChanges[] accountChanges, int count)
    {
        ValueHash256[] hashes = new ValueHash256[count];
        for (int i = 0; i < count; i++)
            hashes[i] = KeccakCache.Compute(accountChanges[i].Address.Bytes);

        return RadixSortByHash(hashes, count);
    }

    private static int[] RadixSortStorageSlots(UInt256[] slotIndices, int count)
    {
        ValueHash256[] hashes = new ValueHash256[count];
        for (int i = 0; i < count; i++)
            StorageTree.ComputeKeyWithLookup(in slotIndices[i], ref hashes[i]);

        return RadixSortByHash(hashes, count);
    }

    private static int[] RadixSortByHash(ValueHash256[] hashes, int count)
    {
        int[] idx0 = new int[count];
        int[] idx1 = new int[count];
        ValueHash256[] buf = new ValueHash256[count];

        for (int i = 0; i < count; i++)
            idx0[i] = i;

        Span<int> counts = stackalloc int[256];
        bool flipped = false;

        for (int p = 3; p >= 0; p--)
        {
            RadixPassWithIndices(
                flipped ? buf : hashes,
                flipped ? hashes : buf,
                flipped ? idx1 : idx0,
                flipped ? idx0 : idx1,
                count, p, counts);
            flipped = !flipped;
        }

        return flipped ? idx1 : idx0;
    }

    private static void RadixPassWithIndices(
        ReadOnlySpan<ValueHash256> hashSrc, Span<ValueHash256> hashDst,
        ReadOnlySpan<int> idxSrc, Span<int> idxDst,
        int len, int byteIndex, Span<int> counts)
    {
        counts.Clear();
        for (int i = 0; i < len; i++)
            counts[hashSrc[i].BytesAsSpan[byteIndex]]++;

        int total = 0;
        for (int b = 0; b < 256; b++)
        {
            int c = counts[b];
            counts[b] = total;
            total += c;
        }

        for (int i = 0; i < len; i++)
        {
            byte key = hashSrc[i].BytesAsSpan[byteIndex];
            int pos = counts[key]++;
            hashDst[pos] = hashSrc[i];
            idxDst[pos] = idxSrc[i];
        }
    }

    public IWorldStateScopeProvider.ICodeDb CodeDb { get; }

    public int HintSequenceId => _hintSequenceId; // Called by FlatStorageTree

    public bool WarmUpStateTrie(Address address, int sequenceId)
    {
        if (_hintSequenceId != sequenceId || _pausePrewarmer) return false;

        // Note: tree root not changed after writing batch. Also, not cleared. So the result is not correct.
        // this is just for warming up
        _warmupStateTree.WarmUpPath(address.ToAccountPath.Bytes);

        return true;
    }

    public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => CreateStorageTreeImpl(address);

    private FlatStorageTree CreateStorageTreeImpl(Address address)
    {
        ref FlatStorageTree? storage = ref CollectionsMarshal.GetValueRefOrAddDefault(_storages, address, out bool exists);
        if (exists) return storage!;

        Hash256 storageRoot = Get(address)?.StorageRoot ?? Keccak.EmptyTreeHash;
        storage = new FlatStorageTree(
            this,
            _warmer,
            _snapshotBundle,
            _configuration,
            _concurrencyQuota,
            storageRoot,
            address,
            _logManager);

        return storage;
    }

    public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) =>
        new WriteBatch(this, estimatedAccountNum, _logManager.GetClassLogger<WriteBatch>());

    public void Commit(long blockNumber)
    {
        _pausePrewarmer = true;

        using ArrayPoolListRef<Task> commitTask = new(_storages.Count);

        commitTask.Add(Task.Factory.StartNew(() =>
        {
            // Commit will copy the trie nodes from the tree to the bundle.
            // Its fine to commit the state tree together with the storage tree at this point as the storage tree
            // root has been resolved and updated to the state tree within the writebatch.
            _stateTree.Commit();
        }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default));

        foreach (KeyValuePair<AddressAsKey, FlatStorageTree> storage in _storages)
        {
            if (_concurrencyQuota.TryRequestConcurrencyQuota())
            {
                commitTask.Add(Task.Factory.StartNew((ctx) =>
                {
                    FlatStorageTree st = (FlatStorageTree)ctx!;
                    st.CommitTree();
                    _concurrencyQuota.ReturnConcurrencyQuota();
                }, storage.Value, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default));
            }
            else
            {
                storage.Value.CommitTree();
            }
        }

        Task.WaitAll(commitTask.AsSpan());

        _storages.Clear();

        StateId newStateId = new(blockNumber, RootHash);
        bool shouldAddSnapshot = !_isReadOnly && _currentStateId != newStateId;
        (Snapshot? newSnapshot, TransientResource? cachedResource) = _snapshotBundle.CollectAndApplySnapshot(_currentStateId, newStateId, shouldAddSnapshot);

        if (shouldAddSnapshot)
        {
            if (_currentStateId != newStateId)
            {
                _commitTarget.AddSnapshot(newSnapshot!, cachedResource!);
            }
            else
            {
                newSnapshot?.Dispose();
                cachedResource?.Dispose();
            }
        }

        _currentStateId = newStateId;
        _pausePrewarmer = false;
    }

    // Largely same logic as the the one for TrieStoreScopeProvider, but more confusing when deduplicated.
    // So I just leave it here.
    private class WriteBatch(
        FlatWorldStateScope scope,
        int estimatedAccountCount,
        ILogger logger
    ) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly Dictionary<AddressAsKey, Account?> _dirtyAccounts = new(estimatedAccountCount);
        private readonly ConcurrentQueue<(AddressAsKey, Hash256)> _dirtyStorageTree = new();

        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated;

        public void Set(Address key, Account? account)
        {
            _dirtyAccounts[key] = account;
            scope._snapshotBundle.SetAccount(key, account);

            if (account is null)
            {
                // This may not get called by the storage write batch as the worldstate does not try to update storage
                // at all if the end account is null. This is not a problem for trie, but is a problem for flat.
                scope.CreateStorageTreeImpl(key).SelfDestruct();
            }
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address address, int estimatedEntries) =>
            scope
                .CreateStorageTreeImpl(address)
                .CreateWriteBatch(
                    estimatedEntries: estimatedEntries,
                    onRootUpdated: (address, newRoot) => MarkDirty(address, newRoot));

        private void MarkDirty(AddressAsKey address, Hash256 storageTreeRootHash) =>
            _dirtyStorageTree.Enqueue((address, storageTreeRootHash));

        public void Dispose()
        {
            try
            {
                while (_dirtyStorageTree.TryDequeue(out (AddressAsKey, Hash256) entry))
                {
                    (AddressAsKey key, Hash256 storageRoot) = entry;
                    if (!_dirtyAccounts.TryGetValue(key, out Account? account)) account = scope.Get(key);
                    if (account is null)
                    {
                        if (storageRoot == Keccak.EmptyTreeHash) continue;
                        using IWorldStateScopeProvider.IStorageWriteBatch wb = CreateStorageWriteBatch(entry.Item1, 0);
                        wb.Clear();
                        continue;
                    }
                    account = account.WithChangedStorageRoot(storageRoot);
                    _dirtyAccounts[key] = account;

                    scope._snapshotBundle.SetAccount(key, account);

                    OnAccountUpdated?.Invoke(key, new IWorldStateScopeProvider.AccountUpdated(key, account));
                    if (logger.IsTrace) Trace(key, storageRoot, account);
                }

                using StateTree.StateTreeBulkSetter stateSetter = scope._stateTree.BeginSet(_dirtyAccounts.Count);
                foreach (KeyValuePair<AddressAsKey, Account?> kv in _dirtyAccounts)
                {
                    stateSetter.Set(kv.Key, kv.Value);
                }
            }
            finally
            {
                _dirtyAccounts.Clear();

                Interlocked.Increment(ref scope._hintSequenceId);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(Address address, Hash256 storageRoot, Account? account) =>
                logger.Trace($"Update {address} S {account?.StorageRoot} -> {storageRoot}");
        }
    }
}
