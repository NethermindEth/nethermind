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
    MultiVersionMemory multiVersionMemory)
    : IWorldStateScopeProvider
{
    public HashSet<Read<StorageCell>> ReadSet { get; private set; } = null!;
    public Dictionary<StorageCell, object> WriteSet { get; private set; } = null!;

    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        ReadSet = new(); //TODO: object poolling?
        WriteSet = new();
        return new MultiVersionMemoryScope(version, baseProvider.BeginScope(baseBlock), multiVersionMemory, ReadSet, WriteSet);
    }

    private static TResult Get<TStorage, TResult>(
        StorageCell location,
        int txIndex,
        MultiVersionMemory multiVersionMemory,
        HashSet<Read<StorageCell>> readSet,
        TStorage storage,
        Func<TStorage, StorageCell, TResult> getFromStorage)
    {
        Status status = multiVersionMemory.TryRead(location, txIndex, out Version version, out object? value);
        TResult result = status switch
        {
            Status.ReadError => throw new AbortParallelExecutionException(in version),
            Status.NotFound => getFromStorage(storage, location),
            Status.Ok => (TResult)value,
            _ => ThrowArgumentOutOfRangeException()
        };

        readSet.Add(new Read<StorageCell>(location, version));
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
        HashSet<Read<StorageCell>> readSet,
        Dictionary<StorageCell, object> writeSet) : IWorldStateScopeProvider.IScope
    {
        public void Dispose() => baseScope.Dispose();

        public Hash256 RootHash => baseScope.RootHash;

        public void UpdateRootHash() => baseScope.UpdateRootHash();

        public Account? Get(Address address) => Get<IWorldStateScopeProvider.IScope, Account?>(
            new StorageCell(address),
            version.TxIndex,
            multiVersionMemory,
            readSet,
            baseScope,
            static (scope, location) => scope.Get(location.Address));

        public void HintGet(Address address, Account? account) => baseScope.HintGet(address, account);

        public IWorldStateScopeProvider.ICodeDb CodeDb => baseScope.CodeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) =>
            new MultiVersionMemoryStorageTree(address, version.TxIndex, baseScope.CreateStorageTree(address), multiVersionMemory, readSet);

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) =>
            new MultiVersionMemoryWriteBatch(version, multiVersionMemory, readSet, writeSet);

        public void Commit(long blockNumber) => baseScope.Commit(blockNumber);

        private class MultiVersionMemoryWriteBatch(
            Version version,
            MultiVersionMemory multiVersionMemory,
            HashSet<Read<StorageCell>> readSet,
            Dictionary<StorageCell, object> writeSet) : IWorldStateScopeProvider.IWorldStateWriteBatch
        {
            public void Dispose() => multiVersionMemory.Record(version, readSet, writeSet);

            public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated;

            public void Set(Address key, Account? account)
            {
                writeSet[new StorageCell(key)] = account;
                OnAccountUpdated?.Invoke(this, new IWorldStateScopeProvider.AccountUpdated(key, account));
            }

            public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
                new MultiVersionMemoryStorageWriteBatch(key, writeSet);

            private class MultiVersionMemoryStorageWriteBatch(Address address, Dictionary<StorageCell, object> writeSet)
                : IWorldStateScopeProvider.IStorageWriteBatch
            {
                public void Dispose() { }
                public void Set(in UInt256 index, byte[] value) => writeSet[new StorageCell(address, index)] = value;
                public void Clear() => writeSet[new StorageCell(address, Keccak.EmptyTreeHash.ValueHash256)] = MultiVersionMemory.SelfDestructMonit;
            }
        }

        private class MultiVersionMemoryStorageTree(
            Address address,
            int txIndex,
            IWorldStateScopeProvider.IStorageTree baseStorageTree,
            MultiVersionMemory multiVersionMemory,
            HashSet<Read<StorageCell>> readSet)
            : IWorldStateScopeProvider.IStorageTree
        {
            public Hash256 RootHash => throw new NotImplementedException();

            public byte[] Get(in UInt256 index) => Get<IWorldStateScopeProvider.IStorageTree, byte[]>(
                new StorageCell(address, index),
                txIndex,
                multiVersionMemory,
                readSet,
                baseStorageTree,
                static (scope, location) => scope.Get(location.Index));

            public void HintGet(in UInt256 index, byte[]? value) => baseStorageTree.HintGet(in index, value);

            public byte[] Get(in ValueHash256 hash) => Get<IWorldStateScopeProvider.IStorageTree, byte[]>(
                new StorageCell(address, hash),
                txIndex,
                multiVersionMemory,
                readSet,
                baseStorageTree,
                static (scope, location) => scope.Get(location.Hash));
        }
    }
}
