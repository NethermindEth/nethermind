// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;

namespace Nethermind.State;

public class WorldStateMetricsScopeProvider(IWorldStateScopeProvider baseProvider, Action<double> updateMetrics) : IWorldStateScopeProvider
{
    private readonly IWorldStateScopeProvider _baseProvider = baseProvider;
    private readonly Action<double> _updateMetrics = updateMetrics;
    private double _stateMerkleizationTime;

    public bool HasRoot(BlockHeader? baseBlock) => _baseProvider.HasRoot(baseBlock);
    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock) => new MetricsScope(_baseProvider.BeginScope(baseBlock), this);

    private sealed class MetricsScope : IWorldStateScopeProvider.IScope, IUncachedAccountReader, IUncachedStorageTreeProvider
    {
        private readonly IWorldStateScopeProvider.IScope _baseScope;
        private readonly WorldStateMetricsScopeProvider _parent;
        // Capability references resolved once; the per-call cost was a virtual property fetch + interface
        // type-test on every account/storage read through the decorator.
        private readonly IUncachedAccountReader? _uncachedAccountReader;
        private readonly IUncachedStorageTreeProvider? _uncachedStorageTreeProvider;

        public MetricsScope(IWorldStateScopeProvider.IScope baseScope, WorldStateMetricsScopeProvider parent)
        {
            _baseScope = baseScope;
            _parent = parent;
            if (baseScope is IUncachedAccountReader { CanReadAccountUncached: true } uncachedReader)
            {
                _uncachedAccountReader = uncachedReader;
            }
            if (baseScope is IUncachedStorageTreeProvider { CanCreateStorageTreeUncachedAccount: true } uncachedStorage)
            {
                _uncachedStorageTreeProvider = uncachedStorage;
            }
        }

        public void Dispose()
        {
            _baseScope.Dispose();
            _parent._stateMerkleizationTime = 0d;
        }

        public Hash256 RootHash => _baseScope.RootHash;

        public void UpdateRootHash() => _baseScope.UpdateRootHash();

        public Account? Get(Address address) => _baseScope.Get(address);

        public bool CanReadAccountUncached => _uncachedAccountReader is not null;

        public Account? GetAccountUncached(Address address) =>
            _uncachedAccountReader is { } reader ? reader.GetAccountUncached(address) : _baseScope.Get(address);

        public void HintGet(Address address, Account? account) => _baseScope.HintGet(address, account);

        public IWorldStateScopeProvider.ICodeDb CodeDb => _baseScope.CodeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => _baseScope.CreateStorageTree(address);

        public bool CanCreateStorageTreeUncachedAccount => _uncachedStorageTreeProvider is not null;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTreeUncachedAccount(Address address) =>
            _uncachedStorageTreeProvider is { } provider ? provider.CreateStorageTreeUncachedAccount(address) : _baseScope.CreateStorageTree(address);

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => _baseScope.StartWriteBatch(estimatedAccountNum);

        public void Commit(long blockNumber)
        {
            long start = Stopwatch.GetTimestamp();
            _baseScope.Commit(blockNumber);
            _parent._stateMerkleizationTime += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            _parent._updateMetrics(_parent._stateMerkleizationTime);
        }
    }
}
