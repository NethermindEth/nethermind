// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State;

public interface IStateMerkleizationMetrics
{
    double StateMerkleizationTime { get; }
    void ResetStateMerkleizationTime();
}

public class WorldStateMetricsScopeProvider(IWorldStateScopeProvider baseProvider)
    : IWorldStateScopeProvider, IStateMerkleizationMetrics
{
    private double _stateMerkleizationTime;

    public double StateMerkleizationTime => _stateMerkleizationTime;
    public void ResetStateMerkleizationTime() => _stateMerkleizationTime = 0d;

    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);
    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock) => new MetricsScope(baseProvider.BeginScope(baseBlock), this);

    private sealed class MetricsScope(IWorldStateScopeProvider.IScope baseScope, WorldStateMetricsScopeProvider parent) : IWorldStateScopeProvider.IScope
    {
        public void Dispose() => baseScope.Dispose();

        public Hash256 RootHash => baseScope.RootHash;

        public void UpdateRootHash()
        {
            long start = Stopwatch.GetTimestamp();
            baseScope.UpdateRootHash();
            parent._stateMerkleizationTime += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

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
        }
    }
}
