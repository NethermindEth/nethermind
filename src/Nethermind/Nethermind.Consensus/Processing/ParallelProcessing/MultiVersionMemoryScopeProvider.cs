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
    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock) => new MultiVersionMemoryScope(version, baseProvider.BeginScope(baseBlock), multiVersionMemory);

    private static TResult Get<TStorage, TResult>(
        StorageCell location,
        int txIndex,
        MultiVersionMemory multiVersionMemory,
        TStorage storage,
        Func<TStorage, StorageCell, TResult> getFromStorage)
    {
        Status status = multiVersionMemory.TryRead(location, txIndex, out _, out object? value);
        return status switch
        {
            Status.ReadError => throw new AbortParallelExecutionException(),
            Status.NotFound => getFromStorage(storage, location),
            Status.Ok => (TResult)value,
            _ => ThrowArgumentOutOfRangeException()
        };

        [DoesNotReturn]
        [StackTraceHidden]
        TResult ThrowArgumentOutOfRangeException() =>
            throw new ArgumentOutOfRangeException(nameof(status), status, $"Unknown multi version memory read status: {status}");
    }

    private class MultiVersionMemoryScope(
        Version version,
        IWorldStateScopeProvider.IScope baseScope,
        MultiVersionMemory multiVersionMemory) : IWorldStateScopeProvider.IScope
    {
        private readonly HashSet<Read<StorageCell>> _readSet = new();

        public void Dispose() => baseScope.Dispose();

        public Hash256 RootHash => baseScope.RootHash;

        public void UpdateRootHash() => baseScope.UpdateRootHash();

        public Account? Get(Address address) => Get<IWorldStateScopeProvider.IScope, Account?>(
            new StorageCell(address),
            version.TxIndex,
            multiVersionMemory,
            baseScope,
            static (scope, location) => scope.Get(location.Address));

        public void HintGet(Address address, Account? account) => baseScope.HintGet(address, account);

        public IWorldStateScopeProvider.ICodeDb CodeDb => baseScope.CodeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) =>
            new MultiVersionMemoryStorageTree(address, version.TxIndex, baseScope.CreateStorageTree(address), multiVersionMemory);

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => new MultiVersionMemoryWriteBatch(version, multiVersionMemory, _readSet);

        public void Commit(long blockNumber)
        {
            // TODO: push the changes to the tree below first?
            // baseScope.Commit(blockNumber);
        }

        private class MultiVersionMemoryWriteBatch(
            Version version,
            MultiVersionMemory multiVersionMemory,
            HashSet<Read<StorageCell>> readSet) : IWorldStateScopeProvider.IWorldStateWriteBatch
        {
            private readonly Dictionary<StorageCell, object> _writeSet = new();

            public void Dispose()
            {
                multiVersionMemory.Record(version, readSet, _writeSet);
            }

            public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated;

            public void Set(Address key, Account? account) => _writeSet[new StorageCell(key)] = account;

            public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
                new MultiVersionMemoryStorageWriteBatch(key, _writeSet);

            private class MultiVersionMemoryStorageWriteBatch(Address address, Dictionary<StorageCell, object> writeSet)
                : IWorldStateScopeProvider.IStorageWriteBatch
            {
                public void Dispose() { }

                public void Set(in UInt256 index, byte[] value) => writeSet[new StorageCell(address, index)] = value;

                public void Clear()
                {
                    // TODO: proper self-destruct
                }
            }
        }

        private class MultiVersionMemoryStorageTree(
            Address address,
            int txIndex,
            IWorldStateScopeProvider.IStorageTree baseStorageTree,
            MultiVersionMemory multiVersionMemory)
            : IWorldStateScopeProvider.IStorageTree
        {
            public Hash256 RootHash => throw new NotImplementedException();

            public byte[] Get(in UInt256 index) => Get<IWorldStateScopeProvider.IStorageTree, byte[]>(
                new StorageCell(address, index),
                txIndex,
                multiVersionMemory,
                baseStorageTree,
                static (scope, location) => scope.Get(location.Index));

            public void HintGet(in UInt256 index, byte[]? value) => baseStorageTree.HintGet(in index, value);

            public byte[] Get(in ValueHash256 hash) => Get<IWorldStateScopeProvider.IStorageTree, byte[]>(
                new StorageCell(address, hash),
                txIndex,
                multiVersionMemory,
                baseStorageTree,
                static (scope, location) => scope.Get(location.Hash));
        }
    }
}
