// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Store.Test;

public class WorldStateScopeProviderMetricsDecoratorTests
{
    [Test]
    public void Test_TracksUpdateRootHashTime()
    {
        TestMemDb kv = new TestMemDb();
        IWorldStateScopeProvider baseScopeProvider = new TrieStoreScopeProvider(new TestRawTrieStore(kv), new MemDb(), LimboLogs.Instance);
        WorldStateScopeProviderMetricsDecorator metricsDecorator = new(baseScopeProvider);

        metricsDecorator.StateMerkleizationTime.Should().Be(0);

        using (var scope = metricsDecorator.BeginScope(null))
        {
            using (var writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
            }

            scope.UpdateRootHash();
            metricsDecorator.StateMerkleizationTime.Should().BeGreaterThan(0);
        }
    }

    [Test]
    public void Test_TracksCommitTime()
    {
        TestMemDb kv = new TestMemDb();
        IWorldStateScopeProvider baseScopeProvider = new TrieStoreScopeProvider(new TestRawTrieStore(kv), new MemDb(), LimboLogs.Instance);
        WorldStateScopeProviderMetricsDecorator metricsDecorator = new(baseScopeProvider);

        metricsDecorator.StateMerkleizationTime.Should().Be(0);

        using (var scope = metricsDecorator.BeginScope(null))
        {
            using (var writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
            }

            scope.Commit(1);
            metricsDecorator.StateMerkleizationTime.Should().BeGreaterThan(0);
        }
    }

    [Test]
    public void Test_ResetClearsMetrics()
    {
        TestMemDb kv = new TestMemDb();
        IWorldStateScopeProvider baseScopeProvider = new TrieStoreScopeProvider(new TestRawTrieStore(kv), new MemDb(), LimboLogs.Instance);
        WorldStateScopeProviderMetricsDecorator metricsDecorator = new(baseScopeProvider);

        using (var scope = metricsDecorator.BeginScope(null))
        {
            using (var writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
            }

            scope.UpdateRootHash();
        }

        metricsDecorator.StateMerkleizationTime.Should().BeGreaterThan(0);
        metricsDecorator.Reset();
        metricsDecorator.StateMerkleizationTime.Should().Be(0);
    }

    [Test]
    public void Test_AccumulatesTimeAcrossMultipleOperations()
    {
        TestMemDb kv = new TestMemDb();
        IWorldStateScopeProvider baseScopeProvider = new TrieStoreScopeProvider(new TestRawTrieStore(kv), new MemDb(), LimboLogs.Instance);
        WorldStateScopeProviderMetricsDecorator metricsDecorator = new(baseScopeProvider);

        using (var scope = metricsDecorator.BeginScope(null))
        {
            using (var writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
            }

            scope.UpdateRootHash();
            double timeAfterFirstUpdate = metricsDecorator.StateMerkleizationTime;
            timeAfterFirstUpdate.Should().BeGreaterThan(0);

            scope.Commit(1);
            metricsDecorator.StateMerkleizationTime.Should().BeGreaterThan(timeAfterFirstUpdate);
        }
    }

    [Test]
    public void Test_HasRootDelegates()
    {
        TestMemDb kv = new TestMemDb();
        IWorldStateScopeProvider baseScopeProvider = new TrieStoreScopeProvider(new TestRawTrieStore(kv), new MemDb(), LimboLogs.Instance);
        WorldStateScopeProviderMetricsDecorator metricsDecorator = new(baseScopeProvider);

        // HasRoot should delegate to the base provider
        metricsDecorator.HasRoot(null).Should().Be(baseScopeProvider.HasRoot(null));

        Hash256 stateRoot;
        using (var scope = metricsDecorator.BeginScope(null))
        {
            using (var writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
            }
            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        BlockHeader header = Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject;
        metricsDecorator.HasRoot(header).Should().Be(baseScopeProvider.HasRoot(header));
    }
}
