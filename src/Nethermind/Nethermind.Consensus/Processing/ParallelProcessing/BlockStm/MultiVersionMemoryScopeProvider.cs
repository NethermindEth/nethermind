// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;

public class MultiVersionMemoryScopeProvider(
    TxVersion version,
    IWorldStateScopeProvider baseProvider,
    MultiVersionMemory multiVersionMemory,
    FeeAccumulator feeAccumulator)
    : IWorldStateScopeProvider
{
    public HashSet<Read<ParallelStateKey>> ReadSet { get; private set; } = null!;
    public Dictionary<ParallelStateKey, object> WriteSet { get; private set; } = null!;

    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        ReadSet = []; //TODO: object pooling?
        WriteSet = [];
        object writeSetLock = new();
        return new MultiVersionMemoryScope(version, baseProvider.BeginScope(baseBlock), multiVersionMemory, feeAccumulator, ReadSet, WriteSet, writeSetLock);
    }

    private static TResult Get<TStorage, TResult>(
        ParallelStateKey location,
        int txIndex,
        MultiVersionMemory multiVersionMemory,
        HashSet<Read<ParallelStateKey>> readSet,
        TStorage storage,
        Func<TStorage, ParallelStateKey, TResult> getFromStorage)
    {
        Status status = multiVersionMemory.TryRead(location, txIndex, out TxVersion version, out object? value);
        TResult result = status switch
        {
            Status.ReadError => throw new AbortParallelExecutionException(in version),
            Status.NotFound => getFromStorage(storage, location),
            Status.Ok => (TResult)value,
            _ => ThrowArgumentOutOfRangeException()
        };

        readSet.Add(new Read<ParallelStateKey>(location, version));
        return result;

        [DoesNotReturn]
        [StackTraceHidden]
        TResult ThrowArgumentOutOfRangeException() =>
            throw new ArgumentOutOfRangeException(nameof(status), status, $"Unknown multi version memory read status: {status}");
    }

    private class MultiVersionMemoryScope(
        TxVersion version,
        IWorldStateScopeProvider.IScope baseScope,
        MultiVersionMemory multiVersionMemory,
        FeeAccumulator feeAccumulator,
        HashSet<Read<ParallelStateKey>> readSet,
        Dictionary<ParallelStateKey, object> writeSet,
        object writeSetLock) : IWorldStateScopeProvider.IScope
    {
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

            readSet.Add(new Read<ParallelStateKey>(location, readVersion));

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
        public System.Threading.Tasks.Task HintBal(
            Nethermind.Core.BlockAccessLists.ReadOnlyBlockAccessList bal,
            IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) => baseScope.HintBal(bal, sink);

        public IWorldStateScopeProvider.ICodeDb CodeDb => baseScope.CodeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) =>
            new MultiVersionMemoryStorageTree(address, version.TxIndex, baseScope.CreateStorageTree(address), multiVersionMemory, readSet);

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) =>
            new MultiVersionMemoryWriteBatch(writeSet, writeSetLock);

        public void Commit(long blockNumber) => baseScope.Commit(blockNumber);

        private class MultiVersionMemoryWriteBatch(Dictionary<ParallelStateKey, object> writeSet, object writeSetLock)
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
                new MultiVersionMemoryStorageWriteBatch(key, estimatedEntries, writeSet, writeSetLock);

            private class MultiVersionMemoryStorageWriteBatch(
                Address address,
                int estimatedEntries,
                Dictionary<ParallelStateKey, object> writeSet,
                object writeSetLock)
                : IWorldStateScopeProvider.IStorageWriteBatch
            {
                private Dictionary<ParallelStateKey, object>? _localWrites;

                private Dictionary<ParallelStateKey, object> GetLocalWrites()
                {
                    if (_localWrites is not null)
                    {
                        return _localWrites;
                    }

                    _localWrites = estimatedEntries > 0
                        ? new Dictionary<ParallelStateKey, object>(estimatedEntries)
                        : [];

                    return _localWrites;
                }

                public void Set(in UInt256 index, byte[] value) =>
                    GetLocalWrites()[ParallelStateKey.ForStorage(new StorageCell(address, index))] = value;

                public void Clear() =>
                    GetLocalWrites()[ParallelStateKey.ForStorageClear(address)] = MultiVersionMemory.SelfDestructMonit;

                public void Dispose()
                {
                    if (_localWrites is null)
                    {
                        return;
                    }

                    // Merge under lock — storage batches Dispose from parallel workers.
                    lock (writeSetLock)
                    {
                        foreach (KeyValuePair<ParallelStateKey, object> write in _localWrites)
                        {
                            writeSet[write.Key] = write.Value;
                        }
                    }

                    _localWrites = null;
                }
            }
        }

        private class MultiVersionMemoryStorageTree(
            Address address,
            int txIndex,
            IWorldStateScopeProvider.IStorageTree baseStorageTree,
            MultiVersionMemory multiVersionMemory,
            HashSet<Read<ParallelStateKey>> readSet)
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

                readSet.Add(new Read<ParallelStateKey>(location, valueVersion));

                bool hasClear = clearStatus == Status.Ok && ReferenceEquals(clearValue, MultiVersionMemory.SelfDestructMonit);

                // Always record the clearKey dependency so a later concurrent SELFDESTRUCT
                // re-triggers validation, even on base-zero / not-found paths.
                readSet.Add(new Read<ParallelStateKey>(clearKey, clearVersion));

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
                if (candidate.IsEmpty)
                {
                    return false;
                }

                if (current.IsEmpty)
                {
                    return true;
                }

                if (candidate.TxIndex != current.TxIndex)
                {
                    return candidate.TxIndex > current.TxIndex;
                }

                return candidate.Incarnation > current.Incarnation;
            }
        }

        private void AddFeeReadDependencies(FeeRecipientKind feeKind, int startTxIndex, int txIndex)
        {
            for (int i = startTxIndex; i < txIndex; i++)
            {
                if (!feeAccumulator.IsCommitted(i))
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

                readSet.Add(new Read<ParallelStateKey>(feeKey, feeVersion));
            }
        }
    }
}
