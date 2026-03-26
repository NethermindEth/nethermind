// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Store.Test;

[TestFixture(false)]
[TestFixture(true)]
[Parallelizable(ParallelScope.All)]
public class ScopeProviderTests(bool useFlat)
{
    private class Context : IDisposable
    {
        public IWorldStateScopeProvider ScopeProvider { get; }
        public TestMemDb Kv { get; }
        public TestMemDb CodeKv { get; }
        private readonly IContainer _container;

        public Context(bool useFlat, TestMemDb kv = null, TestMemDb codeKv = null)
        {
            if (useFlat)
            {
                (ScopeProvider, _container) = TestWorldStateFactory.CreateFlatScopeProvider();
            }
            else
            {
                Kv = kv ?? new TestMemDb();
                CodeKv = codeKv ?? new TestMemDb();
                ScopeProvider = new TrieStoreScopeProvider(new TestRawTrieStore(Kv), CodeKv, LimboLogs.Instance);
            }
        }

        public void Dispose() => _container?.Dispose();
    }

    [Test]
    public void Test_CanSaveToState()
    {
        using Context ctx = new(useFlat);

        Hash256 stateRoot;
        using (var scope = ctx.ScopeProvider.BeginScope(null))
        {
            scope.Get(TestItem.AddressA).Should().Be(null);
            using (var writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        stateRoot.Should().NotBe(Keccak.EmptyTreeHash);
        if (!useFlat) ctx.Kv.WritesCount.Should().Be(1);

        using (var scope = ctx.ScopeProvider.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            scope.Get(TestItem.AddressA).Balance.Should().Be(100);
        }
    }

    [Test]
    public void Test_CanSaveToStorage()
    {
        using Context ctx = new(useFlat);

        Hash256 stateRoot;
        using (var scope = ctx.ScopeProvider.BeginScope(null))
        {
            scope.Get(TestItem.AddressA).Should().Be(null);

            using (var writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));

                using (var storageSet = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1))
                {
                    storageSet.Set(1, [1, 2, 3]);
                }
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        stateRoot.Should().NotBe(Keccak.EmptyTreeHash);
        if (!useFlat) ctx.Kv.WritesCount.Should().Be(2);

        using (var scope = ctx.ScopeProvider.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            var storage = scope.CreateStorageTree(TestItem.AddressA);
            storage.Get(1).Should().BeEquivalentTo([1, 2, 3]);
        }
    }

    [Test]
    public void Test_CanSaveToCode()
    {
        using Context ctx = new(useFlat);

        using (var scope = ctx.ScopeProvider.BeginScope(null))
        {
            using (var writer = scope.CodeDb.BeginCodeWrite())
            {
                writer.Set(TestItem.KeccakA, [1, 2, 3]);
            }
        }

        if (!useFlat)
        {
            ctx.CodeKv.WritesCount.Should().Be(1);
        }
        else
        {
            using var scope = ctx.ScopeProvider.BeginScope(null);
            scope.CodeDb.GetCode(TestItem.KeccakA).Should().BeEquivalentTo([1, 2, 3]);
        }
    }

    [Test]
    public void Test_NullAccountWithNonEmptyStorageDoesNotThrow()
    {
        using Context ctx = new(useFlat);
        using var scope = ctx.ScopeProvider.BeginScope(null);

        // Simulates the EIP-161 scenario: storage is flushed for an account that was
        // then deleted (set to null) during state commit. The write batch Dispose should
        // skip the storage root update for the deleted account instead of throwing.
        using (var writeBatch = scope.StartWriteBatch(1))
        {
            using (var storageSet = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1))
            {
                storageSet.Set(1, [1, 2, 3]);
            }

            writeBatch.Set(TestItem.AddressA, null);
        }
    }

    [Test]
    public void Test_CrossBlockCache_WriteThroughPreservesValues()
    {
        using Context ctx = new(useFlat);
        SeqlockCache<StorageCell, byte[]> crossBlockCache = new();
        PreBlockCaches preBlockCaches = new();
        PrewarmerScopeProvider provider = new(ctx.ScopeProvider, preBlockCaches, populatePreBlockCache: false, crossBlockCache: crossBlockCache);

        // Block 1: create account + write storage
        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = provider.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch wb = scope.StartWriteBatch(1))
            {
                wb.Set(TestItem.AddressA, new Account(100, 100));
                using (IWorldStateScopeProvider.IStorageWriteBatch swb = wb.CreateStorageWriteBatch(TestItem.AddressA, 1))
                {
                    swb.Set(1, [10, 20, 30]);
                }
            }
            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        // Verify cross-block cache has the written value
        StorageCell cell = new(TestItem.AddressA, 1);
        crossBlockCache.TryGetValue(in cell, out byte[] cached).Should().BeTrue();
        cached.Should().BeEquivalentTo(new byte[] { 10, 20, 30 });

        // Block 2: read storage — should get value from cross-block cache
        using (IWorldStateScopeProvider.IScope scope = provider.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            IWorldStateScopeProvider.IStorageTree storage = scope.CreateStorageTree(TestItem.AddressA);
            storage.Get(1).Should().BeEquivalentTo(new byte[] { 10, 20, 30 });
        }
    }

    [Test]
    public void Test_CrossBlockCache_ClearedOnFailedBlock()
    {
        using Context ctx = new(useFlat);
        SeqlockCache<StorageCell, byte[]> crossBlockCache = new();
        PreBlockCaches preBlockCaches = new();
        PrewarmerScopeProvider provider = new(ctx.ScopeProvider, preBlockCaches, populatePreBlockCache: false, crossBlockCache: crossBlockCache);

        // Block 1: write storage but don't commit (simulate failed block)
        using (IWorldStateScopeProvider.IScope scope = provider.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch wb = scope.StartWriteBatch(1))
            {
                wb.Set(TestItem.AddressA, new Account(100, 100));
                using (IWorldStateScopeProvider.IStorageWriteBatch swb = wb.CreateStorageWriteBatch(TestItem.AddressA, 1))
                {
                    swb.Set(1, [10, 20, 30]);
                }
            }
            // No commit — scope Dispose should clear the cache
        }

        // Cache should be cleared
        StorageCell cell = new(TestItem.AddressA, 1);
        crossBlockCache.TryGetValue(in cell, out _).Should().BeFalse();
    }
}
