// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
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
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            scope.Get(TestItem.AddressA).Should().Be(null);
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        stateRoot.Should().NotBe(Keccak.EmptyTreeHash);
        if (!useFlat) ctx.Kv.WritesCount.Should().Be(1);

        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            scope.Get(TestItem.AddressA).Balance.Should().Be(100);
        }
    }

    [Test]
    public void Test_CanSaveToStorage()
    {
        using Context ctx = new(useFlat);

        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            scope.Get(TestItem.AddressA).Should().Be(null);

            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));

                using (IWorldStateScopeProvider.IStorageWriteBatch storageSet = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1))
                {
                    storageSet.Set(1, [1, 2, 3]);
                }
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        stateRoot.Should().NotBe(Keccak.EmptyTreeHash);
        if (!useFlat) ctx.Kv.WritesCount.Should().Be(2);

        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            IWorldStateScopeProvider.IStorageTree storage = scope.CreateStorageTree(TestItem.AddressA);
            storage.Get(1).Should().BeEquivalentTo([1, 2, 3]);
        }
    }

    [Test]
    public void Test_CanSaveToCode()
    {
        using Context ctx = new(useFlat);

        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            using (IWorldStateScopeProvider.ICodeSetter writer = scope.CodeDb.BeginCodeWrite())
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
            using IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null);
            scope.CodeDb.GetCode(TestItem.KeccakA).Should().BeEquivalentTo([1, 2, 3]);
        }
    }

    [Test]
    public void Test_NullAccountWithNonEmptyStorageDoesNotThrow()
    {
        using Context ctx = new(useFlat);
        using IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null);

        // Simulates the EIP-161 scenario: storage is flushed for an account that was
        // then deleted (set to null) during state commit. The write batch Dispose should
        // skip the storage root update for the deleted account instead of throwing.
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            using (IWorldStateScopeProvider.IStorageWriteBatch storageSet = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1))
            {
                storageSet.Set(1, [1, 2, 3]);
            }

            writeBatch.Set(TestItem.AddressA, null);
        }
    }

    [TestCase(true, true, 0, 1, 0, 1)]
    [TestCase(true, false, 1, 0, 1, 0)]
    [TestCase(false, true, 2, 0, 1, 0)]
    public void PrewarmerScope_UsesUncachedReadersOnlyInsideExplicitWarmup(
        bool populatePreBlockCache,
        bool beginPreBlockCacheWarmup,
        int expectedGetCount,
        int expectedUncachedGetCount,
        int expectedCreateStorageTreeCount,
        int expectedUncachedCreateStorageTreeCount)
    {
        PreBlockCaches preBlockCaches = new();
        CountingScopeProvider baseProvider = new();
        PrewarmerScopeProvider provider = new(baseProvider, preBlockCaches, populatePreBlockCache);

        if (beginPreBlockCacheWarmup)
        {
            using IPreBlockCacheWarmupSession warmup = ((IPreBlockCacheWarmup)provider).BeginPreBlockCacheWarmup(null);
            warmup.WarmUp(TestItem.AddressA).Should().BeTrue();
            warmup.WarmUp(TestItem.AddressA).Should().BeTrue();
            warmup.Get(new StorageCell(TestItem.AddressA, 1)).Length.Should().Be(0);
        }
        else
        {
            using IWorldStateScopeProvider.IScope scope = provider.BeginScope(null);
            scope.Get(TestItem.AddressA).Should().Be(baseProvider.Scope.Account);
            scope.Get(TestItem.AddressA).Should().Be(baseProvider.Scope.Account);
            scope.CreateStorageTree(TestItem.AddressA).RootHash.Should().Be(baseProvider.Scope.StorageTree.RootHash);
        }

        baseProvider.Scope.GetCount.Should().Be(expectedGetCount);
        baseProvider.Scope.UncachedGetCount.Should().Be(expectedUncachedGetCount);
        baseProvider.Scope.CreateStorageTreeCount.Should().Be(expectedCreateStorageTreeCount);
        baseProvider.Scope.UncachedCreateStorageTreeCount.Should().Be(expectedUncachedCreateStorageTreeCount);
    }

    private sealed class CountingScopeProvider : IWorldStateScopeProvider
    {
        public CountingScope Scope { get; } = new();

        public bool HasRoot(BlockHeader baseBlock) => true;

        public IWorldStateScopeProvider.IScope BeginScope(BlockHeader baseBlock) => Scope;
    }

    private sealed class CountingScope : IWorldStateScopeProvider.IScope, IUncachedAccountReader, IUncachedStorageTreeProvider
    {
        public Account Account { get; } = new(1);
        public IWorldStateScopeProvider.IStorageTree StorageTree { get; } = new CountingStorageTree();
        public int GetCount { get; private set; }
        public int UncachedGetCount { get; private set; }
        public int CreateStorageTreeCount { get; private set; }
        public int UncachedCreateStorageTreeCount { get; private set; }

        public void Dispose()
        {
        }

        public Hash256 RootHash => Keccak.EmptyTreeHash;

        public void UpdateRootHash()
        {
        }

        public Account Get(Address address)
        {
            GetCount++;
            return Account;
        }

        public bool CanReadAccountUncached => true;

        public Account GetAccountUncached(Address address)
        {
            UncachedGetCount++;
            return Account;
        }

        public void HintGet(Address address, Account account)
        {
        }

        public IWorldStateScopeProvider.ICodeDb CodeDb => NullCodeDb.Instance;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
        {
            CreateStorageTreeCount++;
            return StorageTree;
        }

        public bool CanCreateStorageTreeUncachedAccount => true;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTreeUncachedAccount(Address address)
        {
            UncachedCreateStorageTreeCount++;
            return StorageTree;
        }

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => throw new NotImplementedException();

        public void Commit(long blockNumber)
        {
        }
    }

    private sealed class NullCodeDb : IWorldStateScopeProvider.ICodeDb
    {
        public static NullCodeDb Instance { get; } = new();

        public byte[] GetCode(in ValueHash256 codeHash) => [];

        public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => throw new NotImplementedException();
    }

    private sealed class CountingStorageTree : IWorldStateScopeProvider.IStorageTree
    {
        public Hash256 RootHash => Keccak.EmptyTreeHash;

        public byte[] Get(in UInt256 index) => [];

        public void HintSet(in UInt256 index, byte[] value)
        {
        }

        public byte[] Get(in ValueHash256 hash) => [];
    }
}
