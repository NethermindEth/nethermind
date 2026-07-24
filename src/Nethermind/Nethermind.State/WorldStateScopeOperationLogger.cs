// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.State;

public class WorldStateScopeOperationLogger(IWorldStateScopeProvider baseScopeProvider, ILogManager logManager) : IWorldStateScopeProvider
{
    private ILogger _logger = logManager.GetClassLogger<WorldStateScopeOperationLogger>();
    private long _currentScopeId = 0;

    public bool HasRoot(BlockHeader? baseBlock) =>
        baseScopeProvider.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock, LocalMetrics metrics)
    {
        long scopeId = Interlocked.Increment(ref _currentScopeId);
        return new ScopeWrapper(baseScopeProvider.BeginScope(baseBlock, metrics), scopeId, _logger);
    }

    private class ScopeWrapper(IWorldStateScopeProvider.IScope innerScope, long scopeId, ILogger logger) : IWorldStateScopeProvider.IScope
    {
        public void HintWarmAccount(in ValueAddress address) => innerScope.HintWarmAccount(in address);

        public void HintWarmSlot(in ValueAddress address, in UInt256 index) => innerScope.HintWarmSlot(in address, in index);

        public void Dispose()
        {
            innerScope.Dispose();
            logger.Trace($"{scopeId}: Scope disposed");
        }

        public Hash256 RootHash => innerScope.RootHash;

        public void UpdateRootHash()
        {
            innerScope.UpdateRootHash();
            logger.Trace($"{scopeId}: Update root hash");
        }

        public Account? Get(Address address)
        {
            Account? res = innerScope.Get(address);
            logger.Trace($"{scopeId}: Get account {address}, got {res}");
            return res;
        }

        public void HintGet(Address address, Account? account) => innerScope.HintGet(address, account);

        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null)
            => innerScope.HintBal(bal, sink);

        public IWorldStateScopeProvider.ICodeDb CodeDb => innerScope.CodeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) =>
            new StorageTreeWrapper(innerScope.CreateStorageTree(address), address, scopeId, logger);

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) =>
            new WriteBatchWrapper(innerScope.StartWriteBatch(estimatedAccountNum), scopeId, logger);

        public void Commit(ulong blockNumber) => innerScope.Commit(blockNumber);

        public bool ShouldComputeStateRoot(BlockHeader header) => innerScope.ShouldComputeStateRoot(header);
    }

    private class StorageTreeWrapper(IWorldStateScopeProvider.IStorageTree storageTree, Address address, long scopeId, ILogger logger) : IWorldStateScopeProvider.IStorageTree
    {
        public Hash256 RootHash => storageTree.RootHash;

        public byte[] Get(in UInt256 index)
        {
            byte[]? bytes = storageTree.Get(in index);
            logger.Trace($"{scopeId}: S:{address} Get slot {index}, got {bytes?.ToHexString()}");
            return bytes;
        }

        public void HintSet(in UInt256 index, byte[]? value) => storageTree.HintSet(in index, value);
    }

    private class WriteBatchWrapper : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly IWorldStateScopeProvider.IWorldStateWriteBatch _writeBatch;
        private readonly long _scopeId;
        private readonly ILogger _logger1;

        public WriteBatchWrapper(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch, long scopeId, ILogger logger)
        {
            _writeBatch = writeBatch;
            _scopeId = scopeId;
            _logger1 = logger;

            _writeBatch.OnAccountUpdated += (sender, updated) =>
            {
                logger.Trace($"{scopeId}: OnAccountUpdated callback. {updated.Address} -> {updated.Account}");
            };
        }

        public void Dispose()
        {
            _writeBatch.Dispose();
            _logger1.Trace($"{_scopeId}: Write batch disposed");
        }

        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated
        {
            add => _writeBatch.OnAccountUpdated += value;
            remove => _writeBatch.OnAccountUpdated -= value;
        }

        public void Set(Address key, Account? account)
        {
            _writeBatch.Set(key, account);
            _logger1.Trace($"{_scopeId}: Set account {key} to {account}");
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
            new StorageWriteBatchWrapper(_writeBatch.CreateStorageWriteBatch(key, estimatedEntries), key, _scopeId, _logger1);
    }

    private class StorageWriteBatchWrapper(
        IWorldStateScopeProvider.IStorageWriteBatch writeBatch,
        Address address,
        long scopeId,
        ILogger logger) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Dispose()
        {
            writeBatch.Dispose();
            logger.Trace($"{scopeId}: {address}, Storage write batch disposed");
        }

        public void Set(in UInt256 index, byte[] value)
        {
            writeBatch.Set(in index, value);
            logger.Trace($"{scopeId}: {address}, Set {index} to {value?.ToHexString()}");
        }

        public void Clear()
        {
            writeBatch.Clear();
            logger.Trace($"{scopeId}: {address}, Clear");
        }
    }
}
