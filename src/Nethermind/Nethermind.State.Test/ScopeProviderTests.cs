// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;
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

    [TestCase(1)]
    [TestCase(TrieStoreScopeProvider.StorageTreeBulkWriteBatch.MIN_ENTRIES_TO_BATCH + 1)]
    public void Test_CanSaveToStorage(int estimatedEntries)
    {
        using Context ctx = new(useFlat);

        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            Assert.That(scope.Get(TestItem.AddressA), Is.EqualTo(null));

            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));

                using IWorldStateScopeProvider.IStorageWriteBatch storageSet = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, estimatedEntries);
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

    private static readonly StorageCell SlotA1 = new(TestItem.AddressA, 1);
    private static readonly StorageCell SlotC5 = new(TestItem.AddressC, 5);

    private static BlockHeader HeaderAt(Hash256 stateRoot, ulong number) =>
        Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(number).TestObject;

    // A: balance 100 with slot 1 = [10, 20]; B: balance 200 without storage; C: balance 300 with slot 5 = [5].
    private static Hash256 CommitBaseState(Context ctx)
    {
        using IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null);
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(3))
        {
            writeBatch.Set(TestItem.AddressA, new Account(1, 100));
            writeBatch.Set(TestItem.AddressB, new Account(1, 200));
            writeBatch.Set(TestItem.AddressC, new Account(1, 300));
            using IWorldStateScopeProvider.IStorageWriteBatch storageA = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1);
            storageA.Set(SlotA1.Index, [10, 20]);
            using IWorldStateScopeProvider.IStorageWriteBatch storageC = writeBatch.CreateStorageWriteBatch(TestItem.AddressC, 1);
            storageC.Set(SlotC5.Index, [5]);
        }

        scope.Commit(1);
        return scope.RootHash;
    }

    /// <summary>
    /// Models the driver: caches prepared for the base state, then a block-processing world state over a consumer
    /// scope that reads everything into them.
    /// </summary>
    private static (PreBlockCaches Caches, WorldState Consumer) WarmConsumerCaches(Context ctx, Hash256 baseRoot)
    {
        PreBlockCaches caches = new();
        PrewarmerScopeProvider consumerProvider = new(ctx.ScopeProvider, new PrewarmerState(caches, isPrewarmer: false), LimboLogs.Instance);
        WorldState consumer = new(consumerProvider, LimboLogs.Instance);
        caches.PrepareFor(baseRoot);
        using (consumer.BeginScope(HeaderAt(baseRoot, 1)))
        {
            consumer.GetBalance(TestItem.AddressA);
            consumer.GetBalance(TestItem.AddressB);
            consumer.GetBalance(TestItem.AddressC);
            consumer.AccountExists(TestItem.AddressD);
            consumer.Get(in SlotA1);
            consumer.Get(in SlotC5);
        }

        return (caches, consumer);
    }

    /// <summary>A block committed through the consumer, whose final values the world state writes back into the caches.</summary>
    private static Hash256 CommitThroughConsumer(WorldState consumer, Hash256 baseRoot, Action<WorldState> changes)
    {
        using (consumer.BeginScope(HeaderAt(baseRoot, 1)))
        {
            changes(consumer);
            consumer.Commit(Cancun.Instance);
            consumer.CommitTree(2);
            return consumer.StateRoot;
        }
    }

    private static Account CachedAccount(PreBlockCaches caches, Address address)
    {
        AddressAsKey key = address;
        Assert.That(caches.StateCache.TryGetValue(in key, out Account account), Is.True, $"{address} is cached");
        return account;
    }

    private static byte[] CachedSlot(PreBlockCaches caches, in StorageCell cell)
    {
        Assert.That(caches.StorageCache.TryGetValue(in cell, out byte[] value), Is.True, $"{cell} is cached");
        return value;
    }

    [Test]
    public void Test_ConsumerCommit_WritesTheBlocksFinalValuesBackIntoTheCarriedCaches()
    {
        using Context ctx = new(useFlat);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);

        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, ws =>
        {
            ws.AddToBalance(TestItem.AddressA, 300, Cancun.Instance, out _);
            ws.DeleteAccount(TestItem.AddressB);
            ws.Set(in SlotA1, [7]);
        });

        bool carried = caches.PrepareFor(newRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True, "the caches describe the committed state");
            Assert.That(CachedAccount(caches, TestItem.AddressA).Balance, Is.EqualTo((UInt256)400));
            Assert.That(CachedAccount(caches, TestItem.AddressB), Is.Null, "a deleted account is cached as absent");
            Assert.That(CachedAccount(caches, TestItem.AddressC).Balance, Is.EqualTo((UInt256)300), "untouched entries survive");
            Assert.That(CachedSlot(caches, in SlotA1), Is.EqualTo(new byte[] { 7 }));
            Assert.That(CachedSlot(caches, in SlotC5), Is.EqualTo(new byte[] { 5 }), "removing an account without storage clears no storage");
        }

        // The cached account seeds the next block's storage lookups, so its storage root must be the committed one.
        using (consumer.BeginScope(HeaderAt(newRoot, 2)))
        {
            Assert.That(consumer.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)400));
            Assert.That(consumer.Get(in SlotA1).ToArray(), Is.EqualTo(new byte[] { 7 }));
        }
    }

    [Test]
    public void Test_StorageOnlyChange_CachesTheAccountWithItsNewStorageRoot()
    {
        using Context ctx = new(useFlat);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);
        Hash256 baseStorageRoot = CachedAccount(caches, TestItem.AddressA).StorageRoot;

        // No account-level change: the account's new storage root only exists once the storage tree is committed.
        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, ws => ws.Set(in SlotA1, [7]));

        bool carried = caches.PrepareFor(newRoot);

        using IWorldStateScopeProvider.IScope reader = ctx.ScopeProvider.BeginScope(HeaderAt(newRoot, 2));
        Hash256 committedStorageRoot = reader.CreateStorageTree(TestItem.AddressA).RootHash;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True);
            Assert.That(committedStorageRoot, Is.Not.EqualTo(baseStorageRoot), "precondition");
            Assert.That(CachedAccount(caches, TestItem.AddressA).StorageRoot, Is.EqualTo(committedStorageRoot));
            Assert.That(CachedSlot(caches, in SlotA1), Is.EqualTo(new byte[] { 7 }));
        }
    }

    [TestCase(true, TestName = "Test_PrepareFor_TheCommittedRoot_KeepsTheCaches")]
    [TestCase(false, TestName = "Test_PrepareFor_TheParentRoot_ClearsTheCachesThatMovedOn")]
    public void Test_PrepareFor_AfterACommit(bool committedRoot)
    {
        using Context ctx = new(useFlat);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);
        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, ws => ws.AddToBalance(TestItem.AddressA, 300, Cancun.Instance, out _));

        // The commit moved the caches on: a sibling block on the parent finds nothing it can use.
        bool carried = caches.PrepareFor(committedRoot ? newRoot : baseRoot);

        AddressAsKey keyA = TestItem.AddressA;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.EqualTo(committedRoot));
            Assert.That(caches.StateCache.TryGetValue(in keyA, out Account account), Is.EqualTo(committedRoot));
            Assert.That(caches.StorageCache.TryGetValue(in SlotC5, out _), Is.EqualTo(committedRoot));
            if (committedRoot) Assert.That(account.Balance, Is.EqualTo((UInt256)400));
        }
    }

    [TestCase(true, TestName = "Test_StorageClear_OfPreBlockStorage_DropsTheStorageCache")]
    [TestCase(false, TestName = "Test_StorageClear_OfAnAccountWithoutStorage_KeepsTheStorageCache")]
    public void Test_StorageClear(bool preExistingStorage)
    {
        using Context ctx = new(useFlat);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);
        Address cleared = preExistingStorage ? TestItem.AddressA : TestItem.AddressD;
        StorageCell written = new(cleared, 9);
        // Whether the account held storage must come from its tree, not from an evictable cache entry.
        caches.StateCache.Clear();

        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, ws =>
        {
            if (!preExistingStorage) ws.CreateAccount(cleared, 1);
            ws.ClearStorage(cleared);
            ws.Set(in written, [9]);
        });

        bool carried = caches.PrepareFor(newRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True);
            Assert.That(caches.StorageCache.TryGetValue(in SlotC5, out _), Is.EqualTo(!preExistingStorage),
                "unrelated slots survive only when the cleared account had no storage to begin with");
            Assert.That(caches.StorageCache.TryGetValue(in SlotA1, out _), Is.EqualTo(!preExistingStorage),
                "the cleared account's pre-block slots must not survive the clear");
            Assert.That(CachedSlot(caches, in written), Is.EqualTo(new byte[] { 9 }));
        }
    }

    [Test]
    public void Test_DestroyedAccount_DropsItsCachedSlots()
    {
        using Context ctx = new(useFlat);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);

        // Selfdestruct as the transaction processor commits it: the storage is marked destroyed, then the account removed.
        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, ws =>
        {
            ws.GetBalance(TestItem.AddressA);
            ws.MarkStorageDestroyed(TestItem.AddressA);
            ws.DeleteAccount(TestItem.AddressA);
        });
        Assert.That(newRoot, Is.Not.EqualTo(baseRoot), "precondition: the account was removed from the state");

        bool carried = caches.PrepareFor(newRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True);
            Assert.That(CachedAccount(caches, TestItem.AddressA), Is.Null);
            Assert.That(caches.StorageCache.TryGetValue(in SlotA1, out _), Is.False, "the removed account's slots must not survive it");
        }
    }

    [Test]
    public void Test_AccountCreatedAndDestroyedAcrossTwoFlushes_LeavesNoTraceInTheCaches()
    {
        using Context ctx = new(useFlat);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);
        StorageCell slotD1 = new(TestItem.AddressD, 1);

        // Per-transaction root commits, as pre-Byzantium receipts require: the new account's slot reaches the state in
        // the first flush and the account is destroyed before the second. Nothing pre-block is affected.
        Hash256 newRoot;
        using (consumer.BeginScope(HeaderAt(baseRoot, 1)))
        {
            consumer.CreateAccount(TestItem.AddressD, 1);
            consumer.Set(in slotD1, [7]);
            consumer.Commit(Cancun.Instance);
            consumer.MarkStorageDestroyed(TestItem.AddressD);
            consumer.DeleteAccount(TestItem.AddressD);
            consumer.Commit(Cancun.Instance);
            consumer.CommitTree(2);
            newRoot = consumer.StateRoot;
        }

        bool carried = caches.PrepareFor(newRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True);
            Assert.That(CachedAccount(caches, TestItem.AddressD), Is.Null);
            Assert.That(caches.StorageCache.TryGetValue(in slotD1, out _), Is.False, "a slot of the destroyed account must not be cached");
            Assert.That(caches.StorageCache.TryGetValue(in SlotC5, out _), Is.True, "destroying an account created in the block leaves pre-block slots alone");
        }
    }

    [Test]
    public void Test_StorageWrittenForAnAccountThatEndsAbsent_IsNotCached()
    {
        using Context ctx = new(useFlat);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);
        StorageCell slotD1 = new(TestItem.AddressD, 1);

        // The account is removed without a storage clear, as a failed creation is. Its slot never became state, so a
        // later contract at the address must not find it in the caches.
        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, ws =>
        {
            ws.CreateAccount(TestItem.AddressD, 1);
            ws.Set(in slotD1, [7]);
            ws.DeleteAccount(TestItem.AddressD);
            ws.AddToBalance(TestItem.AddressB, 300, Cancun.Instance, out _);
        });

        bool carried = caches.PrepareFor(newRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True);
            Assert.That(CachedAccount(caches, TestItem.AddressD), Is.Null);
            Assert.That(caches.StorageCache.TryGetValue(in slotD1, out _), Is.False, "storage that never became state must not be cached");
            Assert.That(caches.StorageCache.TryGetValue(in SlotC5, out _), Is.True, "an account that never had storage triggers no clear");
            Assert.That(CachedAccount(caches, TestItem.AddressB).Balance, Is.EqualTo((UInt256)500));
        }
    }

    [Test]
    public void Test_DirectStorageRead_OfAnAccountTheBlockNeverLoaded_ClearsNothing()
    {
        using Context ctx = new(useFlat);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);

        // System reads go straight to storage, so the block ends with a storage record for C but no account record.
        // That is not a removal: C keeps its storage, and nothing may be cleared for it.
        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, ws =>
        {
            ws.Get(in SlotC5);
            ws.AddToBalance(TestItem.AddressB, 300, Cancun.Instance, out _);
        });

        bool carried = caches.PrepareFor(newRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True);
            Assert.That(CachedSlot(caches, in SlotC5), Is.EqualTo(new byte[] { 5 }));
            Assert.That(CachedSlot(caches, in SlotA1), Is.EqualTo(new byte[] { 10, 20 }), "no storage clear may be issued for an account the block did not remove");
            Assert.That(CachedAccount(caches, TestItem.AddressC).Balance, Is.EqualTo((UInt256)300));
        }
    }

    [Test]
    public void Test_RevertedStorageClear_CachesTheCommittedValues()
    {
        using Context ctx = new(useFlat);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);

        // A selfdestruct that reverts within its transaction: the clear leaves a conservative mark, but every value the
        // caches end up holding must still be the committed one.
        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, ws =>
        {
            ws.Get(in SlotA1);
            Snapshot snapshot = ws.TakeSnapshot();
            ws.ClearStorage(TestItem.AddressA);
            ws.Restore(snapshot);
            ws.Set(in SlotA1, [7]);
        });

        bool carried = caches.PrepareFor(newRoot);

        using (consumer.BeginScope(HeaderAt(newRoot, 2)))
        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True);
            Assert.That(CachedSlot(caches, in SlotA1), Is.EqualTo(new byte[] { 7 }));
            Assert.That(consumer.Get(in SlotA1).ToArray(), Is.EqualTo(new byte[] { 7 }));
            Assert.That(CachedAccount(caches, TestItem.AddressA).StorageRoot, Is.Not.EqualTo(Keccak.EmptyTreeHash), "the account keeps its storage");
        }
    }

    [Test]
    public void Test_SlotSetToZero_IsCachedAsZero()
    {
        using Context ctx = new(useFlat);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);

        // A zero write is a delete for the tree, but a read of the slot must still be served as zero, not as a miss.
        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, ws => ws.Set(in SlotA1, [0]));

        bool carried = caches.PrepareFor(newRoot);

        using (consumer.BeginScope(HeaderAt(newRoot, 2)))
        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True);
            Assert.That(CachedSlot(caches, in SlotA1).IsZero(), Is.True);
            Assert.That(consumer.Get(in SlotA1).IsZero(), Is.True);
        }
    }

    [Test]
    public void Test_ScopeEndingWithoutACommit_LeavesTheCachesAtTheBaseState()
    {
        using Context ctx = new(useFlat);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);

        // A block that flushed its writes but was thrown away before the tree commit, as on a failed or retried block.
        using (consumer.BeginScope(HeaderAt(baseRoot, 1)))
        {
            consumer.AddToBalance(TestItem.AddressA, 899, Cancun.Instance, out _);
            consumer.Commit(Cancun.Instance);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(caches.ValidFor, Is.EqualTo(baseRoot), "nothing was committed, so the caches still describe the parent");
            Assert.That(CachedAccount(caches, TestItem.AddressA).Balance, Is.EqualTo((UInt256)100));
        }

        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, ws => ws.AddToBalance(TestItem.AddressB, 300, Cancun.Instance, out _));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(caches.PrepareFor(newRoot), Is.True);
            Assert.That(CachedAccount(caches, TestItem.AddressA).Balance, Is.EqualTo((UInt256)100));
            Assert.That(CachedAccount(caches, TestItem.AddressB).Balance, Is.EqualTo((UInt256)500));
        }
    }

    [Test]
    public void Test_WriteBack_OnlyFromTheStateTheCachesDescribe()
    {
        PreBlockCaches caches = new();
        AddressAsKey key = TestItem.AddressA;
        caches.PrepareFor(TestItem.KeccakA);
        caches.StateCache.Set(in key, new Account(1, 100));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(caches.BeginWriteBack(TestItem.KeccakB, TestItem.KeccakC), Is.Null, "a block on another state has nothing to bring forward");
            Assert.That(caches.ValidFor, Is.EqualTo(TestItem.KeccakA));
        }

        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBack = caches.BeginWriteBack(TestItem.KeccakA, TestItem.KeccakC))
        {
            writeBack.Set(TestItem.AddressA, new Account(2, 400));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(caches.ValidFor, Is.EqualTo(TestItem.KeccakC), "disposing the batch moves the caches to the committed state");
            Assert.That(CachedAccount(caches, TestItem.AddressA).Balance, Is.EqualTo((UInt256)400));
            Assert.That(caches.PrepareFor(TestItem.KeccakC), Is.True);
        }
    }

    [Test]
    public void Test_ConsumerScope_OpeningFailure_LeavesNothingBehind()
    {
        PreBlockCaches caches = new();
        caches.ConsumerScopeOpened += () => throw new InvalidOperationException("join failed");
        IWorldStateScopeProvider.IScope baseScope = Substitute.For<IWorldStateScopeProvider.IScope>();
        IWorldStateScopeProvider baseProvider = Substitute.For<IWorldStateScopeProvider>();
        baseProvider.BeginScope(Arg.Any<BlockHeader>(), Arg.Any<LocalMetrics>()).Returns(baseScope);
        PrewarmerScopeProvider consumer = new(baseProvider, new PrewarmerState(caches, isPrewarmer: false), LimboLogs.Instance);

        Assert.That(() => consumer.BeginScope(Build.A.BlockHeader.TestObject), Throws.InvalidOperationException);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(caches.ConsumerScopeOpen, Is.False, "a failed opening must not leave the speculative gate closed");
            Assert.That(caches.MainScope, Is.Null);
        }
        baseScope.Received(1).Dispose();
    }

    [Test]
    public void Test_ConsumerScope_AtAnotherState_ClearsStaleCaches()
    {
        using Context ctx = new(useFlat);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);
        Hash256 otherRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(HeaderAt(baseRoot, 1)))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressC, new Account(5, 5));
            }

            scope.Commit(2);
            otherRoot = scope.RootHash;
        }

        using (consumer.BeginScope(HeaderAt(otherRoot, 2)))
        {
            AddressAsKey keyA = TestItem.AddressA;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(caches.StateCache.TryGetValue(in keyA, out _), Is.False, "entries of another state must not be read");
                Assert.That(caches.ValidFor, Is.Null, "only the driver may vouch for the caches once populators are joined");
                Assert.That(consumer.GetBalance(TestItem.AddressC), Is.EqualTo((UInt256)5));
            }
        }
    }

    [Test]
    public void Test_ConsumerScope_StaysOpenUntilTheUnderlyingScopeIsDisposed()
    {
        PreBlockCaches caches = new();
        IWorldStateScopeProvider.IScope baseScope = Substitute.For<IWorldStateScopeProvider.IScope>();
        bool openDuringBaseDispose = false;
        baseScope.When(s => s.Dispose()).Do(_ => openDuringBaseDispose = caches.ConsumerScopeOpen);
        IWorldStateScopeProvider baseProvider = Substitute.For<IWorldStateScopeProvider>();
        baseProvider.BeginScope(Arg.Any<BlockHeader>(), Arg.Any<LocalMetrics>()).Returns(baseScope);
        PrewarmerScopeProvider consumer = new(baseProvider, new PrewarmerState(caches, isPrewarmer: false), LimboLogs.Instance);

        using (consumer.BeginScope(Build.A.BlockHeader.TestObject))
        {
            Assert.That(caches.ConsumerScopeOpen, Is.True);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(openDuringBaseDispose, Is.True, "the underlying scope drains its background readers on dispose, so sessions stay excluded until then");
            Assert.That(caches.ConsumerScopeOpen, Is.False);
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
        PrewarmerScopeProvider prewarmer = new(ctx.ScopeProvider, new PrewarmerState(caches, isPrewarmer: false), LimboLogs.Instance);

        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageReads(1).TestObject)
            .TestObject;

        using (IWorldStateScopeProvider.IScope scope = prewarmer.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            Assert.DoesNotThrow(() => scope.HintBal(bal));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Test_MainScope_RegisteredForConsumerScopeLifetime(bool isPrewarmer)
    {
        using Context ctx = new(useFlat);

        PreBlockCaches caches = new();
        PrewarmerScopeProvider provider = new(ctx.ScopeProvider, new PrewarmerState(caches, isPrewarmer), LimboLogs.Instance);

        using (IWorldStateScopeProvider.IScope scope = provider.BeginScope(null))
        {
            if (!isPrewarmer)
                Assert.That(caches.MainScope, Is.Not.Null);
            else
                Assert.That(caches.MainScope, Is.Null);
        }

        Assert.That(caches.MainScope, Is.Null, "scope must be unregistered when disposed");
    }

    [Test]
    public void Test_ScopeDecorators_ForwardWarmHints()
    {
        IWorldStateScopeProvider.IScope inner = Substitute.For<IWorldStateScopeProvider.IScope>();
        IWorldStateScopeProvider innerProvider = Substitute.For<IWorldStateScopeProvider>();
        innerProvider.BeginScope(Arg.Any<BlockHeader>(), Arg.Any<LocalMetrics>()).Returns(inner);

        IWorldStateScopeProvider decorated = new WorldStateMetricsScopeProvider(
            new WorldStateScopeOperationLogger(innerProvider, LimboLogs.Instance), _ => { });

        PreBlockCaches caches = new();
        PrewarmerScopeProvider main = new(decorated, new PrewarmerState(caches, isPrewarmer: false), LimboLogs.Instance);

        ValueAddress addressA = new(TestItem.AddressA.Bytes);
        using (main.BeginScope(null))
        {
            caches.MainScope.HintWarmAccount(in addressA);
            caches.MainScope.HintWarmSlot(in addressA, (UInt256)1);
        }

        inner.Received(1).HintWarmAccount(addressA);
        inner.Received(1).HintWarmSlot(addressA, (UInt256)1);
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
        IWorldStateScopeProvider.IScope mainScope = Substitute.For<IWorldStateScopeProvider.IScope>();
        caches.MainScope = mainScope;
        PrewarmerScopeProvider populator = new(ctx.ScopeProvider, new PrewarmerState(caches, isPrewarmer: true), LimboLogs.Instance);

        BlockHeader baseBlock = Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject;
        using (IWorldStateScopeProvider.IScope scope = populator.BeginScope(baseBlock))
        {
            caches.MainScope = null;
            scope.Get(TestItem.AddressA);
            scope.Get(TestItem.AddressA);
        }

        mainScope.Received(1).HintWarmAccount(new ValueAddress(TestItem.AddressA.Bytes));
    }

    [Test]
    public void Test_PopulatorHintWarmSlot_RoutesToMainScope()
    {
        using Context ctx = new(useFlat);

        PreBlockCaches caches = new();
        IWorldStateScopeProvider.IScope mainScope = Substitute.For<IWorldStateScopeProvider.IScope>();
        caches.MainScope = mainScope;
        PrewarmerScopeProvider populator = new(ctx.ScopeProvider, new PrewarmerState(caches, isPrewarmer: true), LimboLogs.Instance);

        ValueAddress addressA = new(TestItem.AddressA.Bytes);
        using (IWorldStateScopeProvider.IScope scope = populator.BeginScope(null))
        {
            caches.MainScope = null;
            scope.HintWarmSlot(in addressA, (UInt256)1);
        }

        mainScope.Received(1).HintWarmSlot(addressA, (UInt256)1);
    }

    [Test]
    public void Test_PreBlockCacheCounters_CountConsumerProbesOnly()
    {
        using Context ctx = new(useFlat);

        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
                using IWorldStateScopeProvider.IStorageWriteBatch storage = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1);
                storage.Set(1, [1, 2, 3]);
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        PreBlockCaches caches = new();
        // The driver vouches for the caches before any populator fills them.
        caches.PrepareFor(stateRoot);
        BlockHeader baseBlock = Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject;

        // Populator probes must not move the pre-block counters: populators miss by design while
        // filling the cache, so counting them would skew the exported coverage ratio.
        LocalMetrics populatorMetrics = new();
        PrewarmerScopeProvider populator = new(ctx.ScopeProvider, new PrewarmerState(caches, isPrewarmer: true), LimboLogs.Instance);
        using (IWorldStateScopeProvider.IScope scope = populator.BeginScope(baseBlock, populatorMetrics))
        {
            scope.Get(TestItem.AddressA);
            scope.CreateStorageTree(TestItem.AddressA).Get(1);
        }

        Assert.That(populatorMetrics.PreBlockAccountHits + populatorMetrics.PreBlockAccountMisses, Is.Zero);
        Assert.That(populatorMetrics.PreBlockStorageHits + populatorMetrics.PreBlockStorageMisses, Is.Zero);

        // Consumer probes count: AddressA / slot 1 were just populated (hits); AddressB / slot 2 are cold (misses).
        LocalMetrics consumerMetrics = new();
        PrewarmerScopeProvider consumer = new(ctx.ScopeProvider, new PrewarmerState(caches, isPrewarmer: false), LimboLogs.Instance);
        using (IWorldStateScopeProvider.IScope scope = consumer.BeginScope(baseBlock, consumerMetrics))
        {
            scope.Get(TestItem.AddressA);
            scope.Get(TestItem.AddressB);
            IWorldStateScopeProvider.IStorageTree storage = scope.CreateStorageTree(TestItem.AddressA);
            storage.Get(1);
            storage.Get(2);
        }

        Assert.That(consumerMetrics.PreBlockAccountHits, Is.EqualTo(1));
        Assert.That(consumerMetrics.PreBlockAccountMisses, Is.EqualTo(1));
        Assert.That(consumerMetrics.PreBlockStorageHits, Is.EqualTo(1));
        Assert.That(consumerMetrics.PreBlockStorageMisses, Is.EqualTo(1));
    }

    [Test]
    public void Test_PopulatorStorageCapture_SkipsBackingReadWithoutCachingSpeculativeValue()
    {
        using Context ctx = new(useFlat);

        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
                using IWorldStateScopeProvider.IStorageWriteBatch storage = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1);
                storage.Set(1, [10, 20]);
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        PreBlockCaches caches = new();
        PrewarmerScopeProvider populator = new(ctx.ScopeProvider, new PrewarmerState(caches, isPrewarmer: true), LimboLogs.Instance);
        BlockHeader baseBlock = Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject;
        StorageCell cell = new(TestItem.AddressA, 1);

        using (PreBlockCaches.StorageReadCapture capture = caches.BeginStorageReadCapture(new StrongBox<int>(16)))
        {
            using IWorldStateScopeProvider.IScope readScope = populator.BeginScope(baseBlock);
            IWorldStateScopeProvider.IStorageTree capturedStorageTree = readScope.CreateStorageTree(TestItem.AddressA);
            Assert.That(capturedStorageTree.Get(1), Is.EqualTo(new byte[] { 1 }));
            Assert.That(capture.Cells, Does.Contain(cell));
        }

        Assert.That(caches.StorageCache.TryGetValue(in cell, out _), Is.False);
        using IWorldStateScopeProvider.IScope uncapturedReadScope = populator.BeginScope(baseBlock);
        IWorldStateScopeProvider.IStorageTree uncapturedStorageTree = uncapturedReadScope.CreateStorageTree(TestItem.AddressA);
        Assert.That(uncapturedStorageTree.Get(1), Is.EqualTo(new byte[] { 10, 20 }));
        Assert.That(caches.StorageCache.TryGetValue(in cell, out byte[] cached), Is.True);
        Assert.That(cached, Is.EqualTo(new byte[] { 10, 20 }));
    }

    [Test]
    public void Test_FlatScope_TrieWarmHints_Smoke()
    {
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
        PrewarmerScopeProvider main = new(ctx.ScopeProvider, new PrewarmerState(caches, isPrewarmer: false), LimboLogs.Instance);

        BlockHeader baseBlock = Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject;
        using (IWorldStateScopeProvider.IScope scope = main.BeginScope(baseBlock))
        {
            Assert.DoesNotThrow(() =>
            {
                ValueAddress addressA = new(TestItem.AddressA.Bytes);
                ValueAddress addressB = new(TestItem.AddressB.Bytes);
                ValueAddress addressC = new(TestItem.AddressC.Bytes);
                scope.HintWarmAccount(in addressA);
                scope.HintWarmSlot(in addressA, 1);
                scope.HintWarmSlot(in addressB, 1);
                scope.HintWarmSlot(in addressC, 1);
                scope.HintWarmAccount(in addressA);
                scope.HintWarmSlot(in addressA, 1);
            });
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
