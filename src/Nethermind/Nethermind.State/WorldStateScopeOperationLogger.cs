// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
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

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        long scopeId = Interlocked.Increment(ref _currentScopeId);
        return new ScopeWrapper(baseScopeProvider.BeginScope(baseBlock), scopeId, _logger);
    }

    private class ScopeWrapper : IWorldStateScopeProvider.IScope, IUncachedAccountReader, IUncachedStorageTreeProvider
    {
        private readonly IWorldStateScopeProvider.IScope _innerScope;
        private readonly long _scopeId;
        private readonly ILogger _logger;
        // See WorldStateMetricsScopeProvider.MetricsScope - capability references resolved once.
        private readonly IUncachedAccountReader? _uncachedAccountReader;
        private readonly IUncachedStorageTreeProvider? _uncachedStorageTreeProvider;

        public ScopeWrapper(IWorldStateScopeProvider.IScope innerScope, long scopeId, ILogger logger)
        {
            _innerScope = innerScope;
            _scopeId = scopeId;
            _logger = logger;
            if (innerScope is IUncachedAccountReader { CanReadAccountUncached: true } uncachedReader)
            {
                _uncachedAccountReader = uncachedReader;
            }
            if (innerScope is IUncachedStorageTreeProvider { CanCreateStorageTreeUncachedAccount: true } uncachedStorage)
            {
                _uncachedStorageTreeProvider = uncachedStorage;
            }
        }

        public void Dispose()
        {
            _innerScope.Dispose();
            _logger.Trace($"{_scopeId}: Scope disposed");
        }

        public Hash256 RootHash => _innerScope.RootHash;

        public void UpdateRootHash()
        {
            _innerScope.UpdateRootHash();
            _logger.Trace($"{_scopeId}: Update root hash");
        }

        public Account? Get(Address address)
        {
            Account? res = _innerScope.Get(address);
            _logger.Trace($"{_scopeId}: Get account {address}, got {res}");
            return res;
        }

        public bool CanReadAccountUncached => _uncachedAccountReader is not null;

        public Account? GetAccountUncached(Address address)
        {
            Account? res = _uncachedAccountReader is { } reader ? reader.GetAccountUncached(address) : _innerScope.Get(address);
            _logger.Trace($"{_scopeId}: Get uncached account {address}, got {res}");
            return res;
        }

        public void HintGet(Address address, Account? account) => _innerScope.HintGet(address, account);

        public IWorldStateScopeProvider.ICodeDb CodeDb => _innerScope.CodeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) =>
            new StorageTreeWrapper(_innerScope.CreateStorageTree(address), address, _scopeId, _logger);

        public bool CanCreateStorageTreeUncachedAccount => _uncachedStorageTreeProvider is not null;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTreeUncachedAccount(Address address)
        {
            IWorldStateScopeProvider.IStorageTree storageTree = _uncachedStorageTreeProvider is { } provider
                ? provider.CreateStorageTreeUncachedAccount(address)
                : _innerScope.CreateStorageTree(address);
            _logger.Trace($"{_scopeId}: Create uncached storage tree {address}");
            return new StorageTreeWrapper(storageTree, address, _scopeId, _logger);
        }

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) =>
            new WriteBatchWrapper(_innerScope.StartWriteBatch(estimatedAccountNum), _scopeId, _logger);

        public void Commit(long blockNumber) => _innerScope.Commit(blockNumber);
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

        public byte[] Get(in ValueHash256 hash)
        {
            byte[]? bytes = storageTree.Get(in hash);
            logger.Trace($"{scopeId}: S:{address} Get slot via hash {hash}, got {bytes?.ToHexString()}");
            return bytes;
        }
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
