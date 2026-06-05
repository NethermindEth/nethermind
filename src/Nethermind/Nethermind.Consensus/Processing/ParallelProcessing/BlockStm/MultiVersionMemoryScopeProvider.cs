// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;

public class MultiVersionMemoryScopeProvider(
    IWorldStateScopeProvider baseProvider,
    MultiVersionMemory multiVersionMemory,
    FeeAccumulator feeAccumulator,
    ConcurrentDictionary<ValueHash256, byte[]> blockCodeWrites)
    : IWorldStateScopeProvider
{
    private const int InitialReadSetCapacity = 32;
    private const int InitialWriteSetCapacity = 16;

    private TxVersion _version;
    private readonly object _writeSetLock = new();
    // MVMM.Record copies entries out; safe to reuse across BeginScope calls.
    private readonly Dictionary<ParallelStateKey, object> _pooledWriteSet = new(InitialWriteSetCapacity);
    private readonly Stack<PooledStorageWriteBatch> _storageBatchPool = new();

    /// <summary>Targets the next <see cref="BeginScope"/> at this tx version.</summary>
    public void SetTxVersion(in TxVersion version) => _version = version;

    public HashSet<Read> ReadSet { get; private set; } = null!;
    public Dictionary<ParallelStateKey, object> WriteSet { get; private set; } = null!;

    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        // ReadSet is retained by MVMM._lastReads for validation; can't safely pool without a hand-off scheme.
        ReadSet = new HashSet<Read>(InitialReadSetCapacity);
        _pooledWriteSet.Clear();
        WriteSet = _pooledWriteSet;
        return new MultiVersionMemoryScope(this, _version, baseProvider.BeginScope(baseBlock), multiVersionMemory, feeAccumulator, ReadSet, WriteSet, _writeSetLock, blockCodeWrites);
    }

    private PooledStorageWriteBatch RentStorageBatch(Address address)
    {
        PooledStorageWriteBatch? batch;
        lock (_storageBatchPool)
        {
            _storageBatchPool.TryPop(out batch);
        }
        batch ??= new PooledStorageWriteBatch(this);
        batch.Init(address);
        return batch;
    }

    private void ReturnStorageBatch(PooledStorageWriteBatch batch)
    {
        lock (_storageBatchPool)
        {
            _storageBatchPool.Push(batch);
        }
    }

    private class MultiVersionMemoryScope(
        MultiVersionMemoryScopeProvider owner,
        TxVersion version,
        IWorldStateScopeProvider.IScope baseScope,
        MultiVersionMemory multiVersionMemory,
        FeeAccumulator feeAccumulator,
        HashSet<Read> readSet,
        Dictionary<ParallelStateKey, object> writeSet,
        object writeSetLock,
        ConcurrentDictionary<ValueHash256, byte[]> blockCodeWrites) : IWorldStateScopeProvider.IScope
    {
        private readonly TrackingCodeDb _codeDb = new(baseScope.CodeDb, blockCodeWrites);

        public void Dispose() => baseScope.Dispose();

        public Hash256 RootHash => baseScope.RootHash;

        public void UpdateRootHash() => baseScope.UpdateRootHash();

        public Account? Get(Address address)
        {
            int txIndex = version.TxIndex;
            ParallelStateKey location = ParallelStateKey.ForAccount(address);

            Status status = multiVersionMemory.TryRead(location, txIndex, out TxVersion readVersion, out object? value);
            Account? result = status switch
            {
                Status.ReadError => throw new AbortParallelExecutionException(in readVersion),
                Status.NotFound => baseScope.Get(address),
                Status.Ok => (Account?)value,
                _ => throw new ArgumentOutOfRangeException(nameof(status), status, $"Unknown multi version memory read status: {status}")
            };

            readSet.Add(new Read(location, readVersion));

            FeeRecipientKind? feeKind = feeAccumulator.GetFeeKind(address);
            if (feeKind == FeeRecipientKind.None)
            {
                return result;
            }

            int startTxIndex = readVersion.IsEmpty ? 0 : readVersion.TxIndex;
            AddFeeReadDependencies(feeKind.Value, startTxIndex, txIndex);

            UInt256 fees = feeAccumulator.GetAccumulatedFees(address, txIndex);
            if (startTxIndex > 0)
            {
                fees -= feeAccumulator.GetAccumulatedFees(address, startTxIndex);
            }

            return fees.IsZero ? result :
                result is null ? new Account(fees) :
                result.WithChangedBalance(result.Balance + fees);
        }

        public void HintGet(Address address, Account? account) => baseScope.HintGet(address, account);

        // STM has no shared cache of its own; forward to the underlying prewarmer.
        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) =>
            baseScope.HintBal(bal, sink);

        public IWorldStateScopeProvider.ICodeDb CodeDb => _codeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) =>
            new MultiVersionMemoryStorageTree(address, version.TxIndex, baseScope.CreateStorageTree(address), multiVersionMemory, readSet);

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) =>
            new MultiVersionMemoryWriteBatch(owner, writeSet, writeSetLock);

        public void Commit(long blockNumber) => baseScope.Commit(blockNumber);

        private class MultiVersionMemoryWriteBatch(
            MultiVersionMemoryScopeProvider owner,
            Dictionary<ParallelStateKey, object> writeSet,
            object writeSetLock)
            : IWorldStateScopeProvider.IWorldStateWriteBatch
        {
            public void Dispose() { }

            public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated;

            public void Set(Address key, Account? account)
            {
                lock (writeSetLock)
                {
                    writeSet[ParallelStateKey.ForAccount(key)] = account!;
                }
                OnAccountUpdated?.Invoke(this, new IWorldStateScopeProvider.AccountUpdated(key, account));
            }

            public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
                owner.RentStorageBatch(key);
        }

        private void AddFeeReadDependencies(FeeRecipientKind feeKind, int startTxIndex, int txIndex)
        {
            // Fast path: if the contiguous-committed prefix covers everything below txIndex,
            // skip the per-i IsCommitted check. ClearFee retreats the prefix before unsetting
            // any flag, so this hint is a safe lower bound.
            bool allPriorCommitted = feeAccumulator.HighestContiguouslyCommitted >= txIndex - 1;

            for (int i = startTxIndex; i < txIndex; i++)
            {
                if (!allPriorCommitted && !feeAccumulator.IsCommitted(i))
                {
                    throw new AbortParallelExecutionException(new TxVersion(i, 0));
                }

                ParallelStateKey feeKey = ParallelStateKey.ForFee(feeKind, i);
                Status status = multiVersionMemory.TryRead(feeKey, txIndex, out TxVersion feeVersion, out _);
                switch (status)
                {
                    case Status.ReadError:
                        throw new AbortParallelExecutionException(in feeVersion);
                    case Status.NotFound:
                        throw new AbortParallelExecutionException(new TxVersion(i, 0));
                }

                readSet.Add(new Read(feeKey, feeVersion));
            }
        }

        private class MultiVersionMemoryStorageTree(
            Address address,
            int txIndex,
            IWorldStateScopeProvider.IStorageTree baseStorageTree,
            MultiVersionMemory multiVersionMemory,
            HashSet<Read> readSet)
            : IWorldStateScopeProvider.IStorageTree
        {
            public Hash256 RootHash => baseStorageTree.RootHash;

            public byte[] Get(in UInt256 index) =>
                GetStorageValue(
                    ParallelStateKey.ForStorage(new StorageCell(address, index)),
                    static (scope, location) => scope.Get(location.StorageCell.Index));

            public void HintSet(in UInt256 index, byte[]? value) => baseStorageTree.HintSet(in index, value);

            public byte[] Get(in ValueHash256 hash) =>
                GetStorageValue(
                    ParallelStateKey.ForStorage(new StorageCell(address, hash)),
                    static (scope, location) => scope.Get(location.StorageCell.Hash));

            private byte[] GetStorageValue(
                ParallelStateKey location,
                Func<IWorldStateScopeProvider.IStorageTree, ParallelStateKey, byte[]> getFromStorage)
            {
                ParallelStateKey clearKey = ParallelStateKey.ForStorageClear(address);

                Status valueStatus = TryRead(location, out TxVersion valueVersion, out object? value);
                Status clearStatus = TryRead(clearKey, out TxVersion clearVersion, out object? clearValue);

                readSet.Add(new Read(location, valueVersion));

                bool hasClear = clearStatus == Status.Ok && ReferenceEquals(clearValue, MultiVersionMemory.SelfDestructMonit);

                // Always record the clearKey dependency so a later concurrent SELFDESTRUCT
                // re-triggers validation, even on base-zero / not-found paths.
                readSet.Add(new Read(clearKey, clearVersion));

                if (valueStatus == Status.Ok)
                {
                    if (hasClear && IsLater(clearVersion, valueVersion))
                    {
                        return VirtualMachineStatics.BytesZero;
                    }
                    return (byte[])value!;
                }

                byte[] baseValue = getFromStorage(baseStorageTree, location);
                return hasClear ? VirtualMachineStatics.BytesZero : baseValue;
            }

            private Status TryRead(ParallelStateKey key, out TxVersion version, out object? value)
            {
                Status status = multiVersionMemory.TryRead(key, txIndex, out version, out value);
                if (status == Status.ReadError)
                {
                    throw new AbortParallelExecutionException(in version);
                }
                return status;
            }

            private static bool IsLater(TxVersion candidate, TxVersion current)
            {
                if (candidate.IsEmpty) return false;
                if (current.IsEmpty) return true;
                if (candidate.TxIndex != current.TxIndex) return candidate.TxIndex > current.TxIndex;
                return candidate.Incarnation > current.Incarnation;
            }
        }
    }

    // Captures code inserts directly into the block-level ConcurrentDictionary so PushChanges
    // can replay them onto the main world state. The resettable world state's codeDb is
    // read-only and drops writes; without this the Account on the main state can't resolve
    // its CodeHash. Codehashes are content-addressed so concurrent writes are idempotent.
    private sealed class TrackingCodeDb(
        IWorldStateScopeProvider.ICodeDb inner,
        ConcurrentDictionary<ValueHash256, byte[]> blockCodeWrites) : IWorldStateScopeProvider.ICodeDb
    {
        public byte[]? GetCode(in ValueHash256 codeHash) =>
            blockCodeWrites.TryGetValue(codeHash, out byte[]? captured) ? captured : inner.GetCode(in codeHash);

        public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => new TrackingSetter(inner.BeginCodeWrite(), blockCodeWrites);

        public bool ContainsCode(in ValueHash256 codeHash) =>
            blockCodeWrites.ContainsKey(codeHash) || inner.ContainsCode(in codeHash);

        public void MarkCodePersisted(in ValueHash256 codeHash) => inner.MarkCodePersisted(in codeHash);

        private sealed class TrackingSetter(
            IWorldStateScopeProvider.ICodeSetter inner,
            ConcurrentDictionary<ValueHash256, byte[]> blockCodeWrites) : IWorldStateScopeProvider.ICodeSetter
        {
            public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code)
            {
                // Capture before forwarding so PushChanges can re-insert onto the main state.
                blockCodeWrites[codeHash] = code.ToArray();
                inner.Set(in codeHash, code);
            }

            public void Dispose() => inner.Dispose();
        }
    }

    /// <summary>Reusable storage write batch; <see cref="Dispose"/> merges into the provider's WriteSet and returns to the pool.</summary>
    private sealed class PooledStorageWriteBatch(MultiVersionMemoryScopeProvider owner)
        : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private Address _address = null!;
        private readonly Dictionary<ParallelStateKey, object> _localWrites = new(16);

        public void Init(Address address) => _address = address;

        public void Set(in UInt256 index, byte[] value) =>
            _localWrites[ParallelStateKey.ForStorage(new StorageCell(_address, index))] = value;

        public void Clear() =>
            _localWrites[ParallelStateKey.ForStorageClear(_address)] = MultiVersionMemory.SelfDestructMonit;

        public void Dispose()
        {
            if (_localWrites.Count > 0)
            {
                lock (owner._writeSetLock)
                {
                    foreach (KeyValuePair<ParallelStateKey, object> write in _localWrites)
                    {
                        owner._pooledWriteSet[write.Key] = write.Value;
                    }
                }
                _localWrites.Clear();
            }
            owner.ReturnStorageBatch(this);
        }
    }
}
