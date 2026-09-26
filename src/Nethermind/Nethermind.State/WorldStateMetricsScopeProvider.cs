// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State;

public class WorldStateMetricsScopeProvider(IWorldStateScopeProvider baseProvider, Action<double> updateMetrics) : IWorldStateScopeProvider
{
    private readonly IWorldStateScopeProvider _baseProvider = baseProvider;
    private readonly Action<double> _updateMetrics = updateMetrics;

    public bool HasRoot(BlockHeader? baseBlock) => _baseProvider.HasRoot(baseBlock);
    public bool SupportsConcurrentScopes => _baseProvider.SupportsConcurrentScopes;

    public bool HasStateForTargetBlock(BlockHeader targetBlock) => _baseProvider.HasStateForTargetBlock(targetBlock);

    public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        if (!_baseProvider.TryBeginScopeAtTarget(targetBlock, metrics, out IWorldStateScopeProvider.IScope? baseScope))
        {
            scope = null;
            return false;
        }

        scope = new MetricsScope(baseScope, this);
        return true;
    }

    public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        if (!_baseProvider.TryBeginScope(baseBlock, metrics, out IWorldStateScopeProvider.IScope? baseScope))
        {
            scope = null;
            return false;
        }

        scope = new MetricsScope(baseScope, this);
        return true;
    }

    private sealed class MetricsScope(IWorldStateScopeProvider.IScope baseScope, WorldStateMetricsScopeProvider parent) : IWorldStateScopeProvider.IScope
    {
        private double _stateMerkleizationTime;

        public void HintWarmAccount(in ValueAddress address) => baseScope.HintWarmAccount(in address);

        public void HintWarmSlot(in ValueAddress address, in UInt256 index) => baseScope.HintWarmSlot(in address, in index);

        public void Dispose() => baseScope.Dispose();

        public Hash256 RootHash => baseScope.RootHash;
        public bool StorageRootsAreAuthoritative => baseScope.StorageRootsAreAuthoritative;

        public void UpdateRootHash() => baseScope.UpdateRootHash();

        public Account? Get(Address address) => baseScope.Get(address);

        public void HintGet(Address address, Account? account) => baseScope.HintGet(address, account);

        public IWorldStateScopeProvider.ICodeDb CodeDb => baseScope.CodeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => baseScope.CreateStorageTree(address);

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => baseScope.StartWriteBatch(estimatedAccountNum);

        public void Commit(ulong blockNumber)
        {
            long start = Stopwatch.GetTimestamp();
            baseScope.Commit(blockNumber);
            _stateMerkleizationTime += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            parent._updateMetrics(_stateMerkleizationTime);
        }

        public void WriteBackCommittedState(Func<IWorldStateScopeProvider.IBlockChangeSnapshot> takeSnapshot) => baseScope.WriteBackCommittedState(takeSnapshot);

        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null)
            => baseScope.HintBal(bal, sink);
    }
}
