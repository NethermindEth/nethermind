// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
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
            Assert.That(scope.Get(TestItem.AddressA), Is.EqualTo(null));
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        Assert.That(stateRoot, Is.Not.EqualTo(Keccak.EmptyTreeHash));
        if (!useFlat) Assert.That(ctx.Kv.WritesCount, Is.EqualTo(1));

        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            Assert.That(scope.Get(TestItem.AddressA).Balance, Is.EqualTo((UInt256)100));
        }
    }

    [Test]
    public void Test_CanSaveToStorage()
    {
        using Context ctx = new(useFlat);

        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            Assert.That(scope.Get(TestItem.AddressA), Is.EqualTo(null));

            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));

                using IWorldStateScopeProvider.IStorageWriteBatch storageSet = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1);
                storageSet.Set(1, [1, 2, 3]);
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        Assert.That(stateRoot, Is.Not.EqualTo(Keccak.EmptyTreeHash));
        if (!useFlat) Assert.That(ctx.Kv.WritesCount, Is.EqualTo(2));

        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            IWorldStateScopeProvider.IStorageTree storage = scope.CreateStorageTree(TestItem.AddressA);
            Assert.That(storage.Get(1), Is.EqualTo([1, 2, 3]));
        }
    }

    [Test]
    public void Test_CanSaveToCode()
    {
        using Context ctx = new(useFlat);

        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            using IWorldStateScopeProvider.ICodeSetter writer = scope.CodeDb.BeginCodeWrite();
            writer.Set(TestItem.KeccakA, [1, 2, 3]);
        }

        if (!useFlat)
        {
            Assert.That(ctx.CodeKv.WritesCount, Is.EqualTo(1));
        }
        else
        {
            using IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null);
            Assert.That(scope.CodeDb.GetCode(TestItem.KeccakA), Is.EqualTo([1, 2, 3]));
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
        using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1);
        using (IWorldStateScopeProvider.IStorageWriteBatch storageSet = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1))
        {
            storageSet.Set(1, [1, 2, 3]);
        }

        writeBatch.Set(TestItem.AddressA, null);
    }

    [Test]
    public void Test_HintBalWithSink_MatchesIndividualReads()
    {
        using Context ctx = new(useFlat);

        // Setup: write accounts with storage
        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(2))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
                writeBatch.Set(TestItem.AddressB, new Account(200, 200));

                using (IWorldStateScopeProvider.IStorageWriteBatch storageA = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 2))
                {
                    storageA.Set(1, [10, 20]);
                    storageA.Set(2, [30, 40]);
                }

                using IWorldStateScopeProvider.IStorageWriteBatch storageB = writeBatch.CreateStorageWriteBatch(TestItem.AddressB, 1);
                storageB.Set(5, [50, 60]);
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        // Build a BAL referencing these accounts and storage slots.
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageReads(1, 2).TestObject,
                Build.An.AccountChanges.WithAddress(TestItem.AddressB).WithStorageReads(5).TestObject,
                Build.An.AccountChanges.WithAddress(TestItem.AddressC).TestObject) // not in state; should be null
            .TestObject;

        // Collect results via HintBal(bal, sink): the merged trie warmup + BAL read pass.
        CollectingBalSink sink = new();
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            scope.HintBal(bal, sink).Wait();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(sink.Accounts.ContainsKey(TestItem.AddressA), Is.True);
                Assert.That(sink.Accounts[TestItem.AddressA]!.Balance, Is.EqualTo((UInt256)100));

                Assert.That(sink.Accounts.ContainsKey(TestItem.AddressB), Is.True);
                Assert.That(sink.Accounts[TestItem.AddressB]!.Balance, Is.EqualTo((UInt256)200));

                Assert.That(sink.NullAccounts.ContainsKey(TestItem.AddressC), Is.True);

                IWorldStateScopeProvider.IStorageTree storageTreeA = scope.CreateStorageTree(TestItem.AddressA);
                IWorldStateScopeProvider.IStorageTree storageTreeB = scope.CreateStorageTree(TestItem.AddressB);

                StorageCell cellA1 = new(TestItem.AddressA, 1);
                StorageCell cellA2 = new(TestItem.AddressA, 2);
                StorageCell cellB5 = new(TestItem.AddressB, 5);

                Assert.That(sink.Storage.ContainsKey(cellA1), Is.True);
                Assert.That(sink.Storage[cellA1], Is.EqualTo(storageTreeA.Get(1)));

                Assert.That(sink.Storage.ContainsKey(cellA2), Is.True);
                Assert.That(sink.Storage[cellA2], Is.EqualTo(storageTreeA.Get(2)));

                Assert.That(sink.Storage.ContainsKey(cellB5), Is.True);
                Assert.That(sink.Storage[cellB5], Is.EqualTo(storageTreeB.Get(5)));
            }
        }
    }

    [TestCase(10)]
    [TestCase(1500)]
    public void Test_HintBalWithSink_BulkSlotReads_MatchesIndividualReads(int slotCount)
    {
        using Context ctx = new(useFlat);

        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));

                using IWorldStateScopeProvider.IStorageWriteBatch storageA = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, slotCount);
                for (int i = 1; i <= slotCount; i++)
                {
                    storageA.Set((UInt256)i, [(byte)i, (byte)(i >> 8)]);
                }
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        UInt256[] readKeys = new UInt256[slotCount];
        for (int i = 1; i <= slotCount; i++) readKeys[i - 1] = (UInt256)i;

        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageReads(readKeys).TestObject)
            .TestObject;

        CollectingBalSink sink = new();
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            scope.HintBal(bal, sink).Wait();

            Assert.That(sink.Storage, Has.Count.EqualTo(slotCount));
            IWorldStateScopeProvider.IStorageTree storageTreeA = scope.CreateStorageTree(TestItem.AddressA);
            for (int i = 1; i <= slotCount; i++)
            {
                StorageCell cell = new(TestItem.AddressA, (UInt256)i);
                Assert.That(sink.Storage[cell], Is.EqualTo(storageTreeA.Get((UInt256)i)), $"slot {i}");
            }
        }
    }

    [Test]
    public void Test_HintBal_DoesNotThrow()
    {
        using Context ctx = new(useFlat);

        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(2))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
                writeBatch.Set(TestItem.AddressB, new Account(200, 200));

                using IWorldStateScopeProvider.IStorageWriteBatch storageA = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1);
                storageA.Set(1, [10, 20]);
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageReads(1).TestObject,
                Build.An.AccountChanges.WithAddress(TestItem.AddressB).TestObject)
            .TestObject;

        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            Assert.DoesNotThrow(() => scope.HintBal(bal));
            // Dispose exits the using; must not throw either (covers the Cancel path).
        }
    }

    [Test]
    public void Test_HintBal_Smoke_PrewarmerWrapped()
    {
        using Context ctx = new(useFlat);

        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
                using IWorldStateScopeProvider.IStorageWriteBatch storageA = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1);
                storageA.Set(1, [10, 20]);
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        // isPrewarmer: false targets the main-processing scope where HintBal actually runs.
        PreBlockCaches caches = new();
        PrewarmerScopeProvider prewarmer = new(ctx.ScopeProvider, caches, LimboLogs.Instance, isPrewarmer: false);

        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageReads(1).TestObject)
            .TestObject;

        using (IWorldStateScopeProvider.IScope scope = prewarmer.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            Assert.DoesNotThrow(() => scope.HintBal(bal));
        }
    }

    [Test]
    public async Task Test_StridePrefetcher_WarmsAheadOfStridingReads()
    {
        const int slotCount = 256;
        UInt256 start = (UInt256)1 << 40; // Above the minimum engagement index.
        UInt256 stride = 7;

        using Context ctx = new(useFlat);

        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
                using IWorldStateScopeProvider.IStorageWriteBatch storageA = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, slotCount);
                UInt256 index = start;
                for (int i = 0; i < slotCount; i++, index += stride)
                {
                    storageA.Set(index, [(byte)(i + 1), (byte)((i >> 8) + 1)]);
                }
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        PreBlockCaches caches = new();
        PrewarmerScopeProvider prewarmer = new(ctx.ScopeProvider, caches, LimboLogs.Instance, isPrewarmer: false);

        using (IWorldStateScopeProvider.IScope scope = prewarmer.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            IWorldStateScopeProvider.IStorageTree storage = scope.CreateStorageTree(TestItem.AddressA);

            // Enough on-pattern reads to engage the prefetcher.
            UInt256 index = start;
            for (int i = 0; i < 12; i++, index += stride)
            {
                storage.Get(in index);
            }

            // A far-ahead slot the consumer never read must show up in the pre-block cache —
            // but only on the flat layout; the trie store cannot host the prefetcher's
            // concurrent read scope, so there the detector must stay inert.
            StorageCell farCell = new(TestItem.AddressA, start + (stride * 200));
            if (useFlat)
            {
                await Eventually.AssertAsync<AssertionException>(() =>
                {
                    Assert.That(caches.StorageCache.TryGetValue(in farCell, out byte[] value), Is.True);
                    Assert.That(value, Is.EqualTo(new byte[] { 201, 1 }));
                }, attempts: 200, delayMilliseconds: 10);
            }
            else
            {
                for (int i = 0; i < 60; i++)
                {
                    Assert.That(caches.StorageCache.TryGetValue(in farCell, out _), Is.False,
                        "Stride prefetcher engaged on a provider without concurrent-scope support.");
                    await Task.Delay(5);
                }
            }
        }
        // Scope disposal joined the reader threads without hanging.
    }

    [Test]
    public async Task Test_StridePrefetcher_DoesNotCacheInBlockWrittenValues()
    {
        UInt256 start = (UInt256)1 << 40; // Above the minimum engagement index.
        UInt256 stride = 7;
        // Beyond the readers' lookahead window at engagement, so it cannot be prefetched before
        // the block's write batch lands.
        UInt256 farIndex = start + (stride * 4500);
        byte[] inBlockValue = [222];

        using Context ctx = new(useFlat);

        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        PreBlockCaches caches = new();
        PrewarmerScopeProvider prewarmer = new(ctx.ScopeProvider, caches, LimboLogs.Instance, isPrewarmer: false);

        using (IWorldStateScopeProvider.IScope scope = prewarmer.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            IWorldStateScopeProvider.IStorageTree storage = scope.CreateStorageTree(TestItem.AddressA);

            // Enough on-pattern reads to engage the prefetcher.
            UInt256 index = start;
            for (int i = 0; i < 12; i++, index += stride)
            {
                storage.Get(in index);
            }

            // Readers are live once a slot inside the initial lookahead window shows up; on the
            // trie layout the prefetcher never engages, and the invariant below holds trivially.
            if (useFlat)
            {
                StorageCell windowCell = new(TestItem.AddressA, start + (stride * 100));
                await Eventually.AssertAsync<AssertionException>(() =>
                {
                    Assert.That(caches.StorageCache.TryGetValue(in windowCell, out _), Is.True);
                }, attempts: 200, delayMilliseconds: 10);
            }

            // The executing block now writes a slot the readers have not reached yet.
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
                using IWorldStateScopeProvider.IStorageWriteBatch storageA = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1);
                storageA.Set(farIndex, inBlockValue);
            }

            // Keep striding past the write so any reader still alive would advance into the
            // written slot and observe the in-block value through a shared live tree.
            for (int i = 0; i < 600; i++, index += stride)
            {
                storage.Get(in index);
            }

            // The prefetcher must never publish a value written by the executing block as parent
            // state; bounded poll because the correct outcome is that nothing ever appears.
            StorageCell farCell = new(TestItem.AddressA, farIndex);
            for (int i = 0; i < 120; i++)
            {
                if (caches.StorageCache.TryGetValue(in farCell, out byte[] cached) &&
                    cached is not null &&
                    cached.AsSpan().SequenceEqual(inBlockValue))
                {
                    Assert.Fail("Stride prefetcher cached a value written by the executing block.");
                }

                await Task.Delay(5);
            }
        }
    }

    [Test]
    public async Task Test_StridePrefetcher_DoesNotEngageAfterFirstCommit()
    {
        UInt256 start = (UInt256)1 << 40; // Above the minimum engagement index.
        UInt256 stride = 7;

        using Context ctx = new(useFlat);

        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        PreBlockCaches caches = new();
        PrewarmerScopeProvider prewarmer = new(ctx.ScopeProvider, caches, LimboLogs.Instance, isPrewarmer: false);

        using (IWorldStateScopeProvider.IScope scope = prewarmer.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            // Sync batches process many blocks through one scope; the scope's parent anchor is
            // only valid for the first of them. Simulate a first block that never touches
            // storage, then a later block issuing a striding scan.
            scope.Commit(1);

            IWorldStateScopeProvider.IStorageTree storage = scope.CreateStorageTree(TestItem.AddressA);
            UInt256 index = start;
            for (int i = 0; i < 12; i++, index += stride)
            {
                storage.Get(in index);
            }

            // Prefetching here would publish values read at the stale anchor; the prefetcher
            // must stay disengaged. Bounded poll because the correct outcome is that nothing
            // ever appears.
            StorageCell farCell = new(TestItem.AddressA, start + (stride * 100));
            for (int i = 0; i < 60; i++)
            {
                Assert.That(caches.StorageCache.TryGetValue(in farCell, out _), Is.False,
                    "Stride prefetcher engaged in a non-first block of a scope.");
                await Task.Delay(5);
            }
        }
    }

#nullable enable
    private class CollectingBalSink : IWorldStateScopeProvider.IAsyncBalReaderSink
    {
        public ConcurrentDictionary<Address, Account> Accounts { get; } = new();
        public ConcurrentDictionary<Address, byte> NullAccounts { get; } = new();
        public ConcurrentDictionary<StorageCell, byte[]> Storage { get; } = new();

        public void OnAccountRead(Address address, Account? account)
        {
            if (account is null)
                NullAccounts[address] = 0;
            else
                Accounts[address] = account;
        }

        public void OnStorageRead(in StorageCell storageCell, byte[] value)
            => Storage[storageCell] = value;
    }
#nullable disable
}
