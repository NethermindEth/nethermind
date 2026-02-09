// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

public class MultiVersionMemoryScopeProvider(
    Version version,
    IWorldStateScopeProvider baseProvider,
    MultiVersionMemory multiVersionMemory,
    FeeAccumulator feeAccumulator)
    : IWorldStateScopeProvider
{
    private readonly FeeAccumulator _feeAccumulator = feeAccumulator;
    public HashSet<Read<ParallelStateKey>> ReadSet { get; private set; } = null!;
    public Dictionary<ParallelStateKey, object> WriteSet { get; private set; } = null!;

    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        ReadSet = new(); //TODO: object poolling?
        WriteSet = new();
        return new MultiVersionMemoryScope(version, baseProvider.BeginScope(baseBlock), multiVersionMemory, _feeAccumulator, ReadSet, WriteSet);
    }

    private static TResult Get<TStorage, TResult>(
        ParallelStateKey location,
        int txIndex,
        MultiVersionMemory multiVersionMemory,
        HashSet<Read<ParallelStateKey>> readSet,
        TStorage storage,
        Func<TStorage, ParallelStateKey, TResult> getFromStorage)
    {
        Status status = multiVersionMemory.TryRead(location, txIndex, out Version version, out object? value);
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
        Version version,
        IWorldStateScopeProvider.IScope baseScope,
        MultiVersionMemory multiVersionMemory,
        FeeAccumulator feeAccumulator,
        HashSet<Read<ParallelStateKey>> readSet,
        Dictionary<ParallelStateKey, object> writeSet) : IWorldStateScopeProvider.IScope
    {
        private readonly FeeAccumulator _feeAccumulator = feeAccumulator;
        public void Dispose() => baseScope.Dispose();

        public Hash256 RootHash => baseScope.RootHash;

        public void UpdateRootHash() => baseScope.UpdateRootHash();

        public Account? Get(Address address)
        {
            int txIndex = version.TxIndex;
            ParallelStateKey location = ParallelStateKey.ForStorage(new StorageCell(address));

            Status status = multiVersionMemory.TryRead(location, txIndex, out Version readVersion, out object? value);
            Account? result = status switch
            {
                Status.ReadError => throw new AbortParallelExecutionException(in readVersion),
                Status.NotFound => baseScope.Get(address),
                Status.Ok => (Account?)value,
                _ => throw new ArgumentOutOfRangeException(nameof(status), status, $"Unknown multi version memory read status: {status}")
            };

            readSet.Add(new Read<ParallelStateKey>(location, readVersion));

            FeeRecipientKind? feeKind = GetFeeKind(address);
            if (feeKind is null)
            {
                return result;
            }

            int startTxIndex = readVersion.IsEmpty ? 0 : readVersion.TxIndex;
            AddFeeReadDependencies(feeKind.Value, startTxIndex, txIndex);

            UInt256 fees = _feeAccumulator.GetAccumulatedFees(address, txIndex);
            if (startTxIndex > 0)
            {
                fees -= _feeAccumulator.GetAccumulatedFees(address, startTxIndex);
            }

            if (fees.IsZero)
            {
                return result;
            }

            return result is null ? new Account(fees) : result.WithChangedBalance(result.Balance + fees);
        }

        public void HintGet(Address address, Account? account) => baseScope.HintGet(address, account);

        public IWorldStateScopeProvider.ICodeDb CodeDb => baseScope.CodeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) =>
            new MultiVersionMemoryStorageTree(address, version.TxIndex, baseScope.CreateStorageTree(address), multiVersionMemory, readSet);

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) =>
            new MultiVersionMemoryWriteBatch(writeSet);

        public void Commit(long blockNumber) => baseScope.Commit(blockNumber);

        private class MultiVersionMemoryWriteBatch(Dictionary<ParallelStateKey, object> writeSet)
            : IWorldStateScopeProvider.IWorldStateWriteBatch
        {
            public void Dispose() { }

            public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated;

            public void Set(Address key, Account? account)
            {
                writeSet[ParallelStateKey.ForStorage(new StorageCell(key))] = account;
                OnAccountUpdated?.Invoke(this, new IWorldStateScopeProvider.AccountUpdated(key, account));
            }

            public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
                new MultiVersionMemoryStorageWriteBatch(key, writeSet);

            private class MultiVersionMemoryStorageWriteBatch(Address address, Dictionary<ParallelStateKey, object> writeSet)
                : IWorldStateScopeProvider.IStorageWriteBatch
            {
                public void Dispose() { }
                public void Set(in UInt256 index, byte[] value) => writeSet[ParallelStateKey.ForStorage(new StorageCell(address, index))] = value;
                public void Clear() => writeSet[ParallelStateKey.ForStorage(new StorageCell(address, Keccak.EmptyTreeHash.ValueHash256))] = MultiVersionMemory.SelfDestructMonit;
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

            public byte[] Get(in UInt256 index) => Get<IWorldStateScopeProvider.IStorageTree, byte[]>(
                ParallelStateKey.ForStorage(new StorageCell(address, index)),
                txIndex,
                multiVersionMemory,
                readSet,
                baseStorageTree,
                static (scope, location) => scope.Get(location.StorageCell.Index));

            public void HintGet(in UInt256 index, byte[]? value) => baseStorageTree.HintGet(in index, value);

            public byte[] Get(in ValueHash256 hash) => Get<IWorldStateScopeProvider.IStorageTree, byte[]>(
                ParallelStateKey.ForStorage(new StorageCell(address, hash)),
                txIndex,
                multiVersionMemory,
                readSet,
                baseStorageTree,
                static (scope, location) => scope.Get(location.StorageCell.Hash));
        }

        private FeeRecipientKind? GetFeeKind(Address address)
        {
            if (_feeAccumulator.GasBeneficiary is not null && address == _feeAccumulator.GasBeneficiary)
            {
                return FeeRecipientKind.GasBeneficiary;
            }

            if (_feeAccumulator.FeeCollector is not null && address == _feeAccumulator.FeeCollector)
            {
                return FeeRecipientKind.FeeCollector;
            }

            return null;
        }

        private void AddFeeReadDependencies(FeeRecipientKind feeKind, int startTxIndex, int txIndex)
        {
            for (int i = startTxIndex; i < txIndex; i++)
            {
                if (!_feeAccumulator.IsCommitted(i))
                {
                    throw new AbortParallelExecutionException(new Version(i, 0));
                }

                ParallelStateKey feeKey = ParallelStateKey.ForFee(feeKind, i);
                Status status = multiVersionMemory.TryRead(feeKey, txIndex, out Version feeVersion, out _);
                switch (status)
                {
                    case Status.ReadError:
                        throw new AbortParallelExecutionException(in feeVersion);
                    case Status.NotFound:
                        throw new AbortParallelExecutionException(new Version(i, 0));
                }

                readSet.Add(new Read<ParallelStateKey>(feeKey, feeVersion));
            }
        }
    }
}
