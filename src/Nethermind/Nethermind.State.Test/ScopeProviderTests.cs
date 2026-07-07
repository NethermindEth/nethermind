// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
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

internal static class ScopeProviderTestExtensions
{
    // Test convenience overload: begins a scope with a throwaway metrics accumulator for tests that
    // call the scope provider directly and do not assert on the folded counters.
    public static IWorldStateScopeProvider.IScope BeginScope(this IWorldStateScopeProvider provider, BlockHeader baseBlock)
        => provider.BeginScope(baseBlock, new LocalMetrics());
}

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

        // Build a BAL referencing these accounts and storage slots
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageReads(1, 2).TestObject,
                Build.An.AccountChanges.WithAddress(TestItem.AddressB).WithStorageReads(5).TestObject,
                Build.An.AccountChanges.WithAddress(TestItem.AddressC).TestObject) // not in state — should be null
            .TestObject;

        // Collect results via HintBal(bal, sink) — the merged trie warmup + BAL read pass
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
            // Dispose exits the using — must not throw either (covers the Cancel path).
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
    public void Test_TrieHintSink_RegisteredForMainScopeLifetime()
    {
        using Context ctx = new(useFlat);

        PreBlockCaches caches = new();
        PrewarmerScopeProvider main = new(ctx.ScopeProvider, caches, LimboLogs.Instance, isPrewarmer: false, registerTrieHintSink: true);

        using (IWorldStateScopeProvider.IScope scope = main.BeginScope(null))
        {
            // Flat scopes support trie warm-up hints; the trie-store backend does not and must stay null.
            if (useFlat)
                Assert.That(caches.TrieHintSink, Is.Not.Null);
            else
                Assert.That(caches.TrieHintSink, Is.Null);
        }

        Assert.That(caches.TrieHintSink, Is.Null, "sink must be unregistered when the scope is disposed");
    }

    [Test]
    public void Test_TrieHintSink_RegisteredThroughScopeDecorators()
    {
        using Context ctx = new(useFlat);

        // The production main-processing chain wraps the backend provider in metrics (and optionally
        // operation-logging) decorators before the prewarmer decorator; the sink lookup must unwrap them.
        IWorldStateScopeProvider decorated = new WorldStateMetricsScopeProvider(
            new WorldStateScopeOperationLogger(ctx.ScopeProvider, LimboLogs.Instance), _ => { });

        PreBlockCaches caches = new();
        PrewarmerScopeProvider main = new(decorated, caches, LimboLogs.Instance, isPrewarmer: false, registerTrieHintSink: true);

        using (IWorldStateScopeProvider.IScope scope = main.BeginScope(null))
        {
            if (useFlat)
                Assert.That(caches.TrieHintSink, Is.Not.Null);
            else
                Assert.That(caches.TrieHintSink, Is.Null);
        }

        Assert.That(caches.TrieHintSink, Is.Null);
    }

    [Test]
    public void Test_TrieHintSink_NotRegisteredWhenDisabledOrPopulator()
    {
        using Context ctx = new(useFlat);

        PreBlockCaches caches = new();

        PrewarmerScopeProvider mainDisabled = new(ctx.ScopeProvider, caches, LimboLogs.Instance, isPrewarmer: false);
        using (mainDisabled.BeginScope(null))
        {
            Assert.That(caches.TrieHintSink, Is.Null);
        }

        PrewarmerScopeProvider populator = new(ctx.ScopeProvider, caches, LimboLogs.Instance, isPrewarmer: true, registerTrieHintSink: true);
        using (populator.BeginScope(null))
        {
            Assert.That(caches.TrieHintSink, Is.Null);
        }
    }

    [Test]
    public void Test_PopulatorGetMiss_PushesAccountTrieWarmHint()
    {
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
        CollectingTrieHintSink sink = new();
        caches.TrieHintSink = sink;
        PrewarmerScopeProvider populator = new(ctx.ScopeProvider, caches, LimboLogs.Instance, isPrewarmer: true);

        BlockHeader baseBlock = Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject;
        using (IWorldStateScopeProvider.IScope scope = populator.BeginScope(baseBlock))
        {
            scope.Get(TestItem.AddressA); // miss: resolves from the base tree and hints
            scope.Get(TestItem.AddressA); // hit: no further hint
        }

        Assert.That(sink.AccountHints, Is.EquivalentTo(new[] { TestItem.AddressA }));
    }

    [Test]
    public void Test_FlatScope_TrieWarmHints_Smoke()
    {
        // Trie warm-up hints are only supported by the flat scope.
        Assume.That(useFlat, Is.True);

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

        PreBlockCaches caches = new();
        PrewarmerScopeProvider main = new(ctx.ScopeProvider, caches, LimboLogs.Instance, isPrewarmer: false, registerTrieHintSink: true);

        BlockHeader baseBlock = Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject;
        using (IWorldStateScopeProvider.IScope scope = main.BeginScope(baseBlock))
        {
            IPrewarmTrieHintSink sink = caches.TrieHintSink;
            Assert.That(sink, Is.Not.Null);

            Assert.DoesNotThrow(() =>
            {
                sink.HintAccountWarm(TestItem.AddressA);
                sink.HintSlotWarm(TestItem.AddressA, 1);
                // Account with an empty pre-state storage root: hint must be dropped without queueing.
                sink.HintSlotWarm(TestItem.AddressB, 1);
                // Unknown account: also dropped.
                sink.HintSlotWarm(TestItem.AddressC, 1);
                // Deduplicated re-hints.
                sink.HintAccountWarm(TestItem.AddressA);
                sink.HintSlotWarm(TestItem.AddressA, 1);
            });
            // Scope dispose waits for outstanding warm-ups, proving the pushed jobs drain cleanly.
        }
    }

    private class CollectingTrieHintSink : IPrewarmTrieHintSink
    {
        public ConcurrentBag<Address> AccountHints { get; } = [];
        public ConcurrentBag<(Address, UInt256)> SlotHints { get; } = [];

        public void HintAccountWarm(Address address) => AccountHints.Add(address);

        public void HintSlotWarm(Address address, in UInt256 index) => SlotHints.Add((address, index));
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
