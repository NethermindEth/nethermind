// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;

namespace Nethermind.State;

public class WorldStateMetricsScopeProvider(IWorldStateScopeProvider baseProvider, Action<double> updateMetrics) : IWorldStateScopeProvider
{
    private readonly IWorldStateScopeProvider _baseProvider = baseProvider;
    private readonly Action<double> _updateMetrics = updateMetrics;
    private double _stateMerkleizationTime;

    public bool HasRoot(BlockHeader? baseBlock) => _baseProvider.HasRoot(baseBlock);
    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock, LocalMetrics metrics) => new MetricsScope(_baseProvider.BeginScope(baseBlock, metrics), this);

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

        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null)
            => baseScope.HintBal(bal, sink);
    }
}
