// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State;

public class WorldStateScopeProviderMetricsDecorator(IWorldStateScopeProvider baseScopeProvider) : IWorldStateScopeProvider
{
    public double StateMerkleizationTime { get; private set; }

    public bool HasRoot(BlockHeader? baseBlock) =>
        baseScopeProvider.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        return new ScopeWrapper(baseScopeProvider.BeginScope(baseBlock), this);
    }

    public void Reset()
    {
        StateMerkleizationTime = 0d;
    }

    private class ScopeWrapper(IWorldStateScopeProvider.IScope innerScope, WorldStateScopeProviderMetricsDecorator parent) : IWorldStateScopeProvider.IScope
    {
        public void Dispose() => innerScope.Dispose();

        public Hash256 RootHash => innerScope.RootHash;

        public void UpdateRootHash()
        {
            long start = Stopwatch.GetTimestamp();
            innerScope.UpdateRootHash();
            parent.StateMerkleizationTime += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        public Account? Get(Address address) => innerScope.Get(address);

        public void HintGet(Address address, Account? account) => innerScope.HintGet(address, account);

        public IWorldStateScopeProvider.ICodeDb CodeDb => innerScope.CodeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => innerScope.CreateStorageTree(address);

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => innerScope.StartWriteBatch(estimatedAccountNum);

        public void Commit(long blockNumber)
        {
            long start = Stopwatch.GetTimestamp();
            innerScope.Commit(blockNumber);
            parent.StateMerkleizationTime += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
    }
}
