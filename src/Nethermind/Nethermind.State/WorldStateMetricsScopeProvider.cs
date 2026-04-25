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

    private sealed class MetricsScope(IWorldStateScopeProvider.IScope baseScope, WorldStateMetricsScopeProvider parent) : IWorldStateScopeProvider.IScope
    {
        public void Dispose()
        {
            baseScope.Dispose();
            parent._stateMerkleizationTime = 0d;
        }

        public Hash256 RootHash => baseScope.RootHash;

        public void UpdateRootHash() => baseScope.UpdateRootHash();

        public Account? Get(Address address) => baseScope.Get(address);

        public void HintGet(Address address, Account? account) => baseScope.HintGet(address, account);

        public IWorldStateScopeProvider.ICodeDb CodeDb => baseScope.CodeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => baseScope.CreateStorageTree(address);

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => baseScope.StartWriteBatch(estimatedAccountNum);

        public void Commit(long blockNumber)
        {
            long start = Stopwatch.GetTimestamp();
            baseScope.Commit(blockNumber);
            parent._stateMerkleizationTime += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            parent._updateMetrics(parent._stateMerkleizationTime);
        }
    }
}
