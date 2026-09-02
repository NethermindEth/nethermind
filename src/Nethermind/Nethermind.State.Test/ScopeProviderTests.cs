// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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

    /// <summary>Models the driver: caches prepared for the base state, then a consumer scope that reads everything into them.</summary>
    private static (PreBlockCaches Caches, PrewarmerScopeProvider Consumer) WarmConsumerCaches(Context ctx, Hash256 baseRoot)
    {
        PreBlockCaches caches = new();
        PrewarmerScopeProvider consumer = new(ctx.ScopeProvider, new PrewarmerState(caches, isPrewarmer: false), LimboLogs.Instance);
        caches.PrepareFor(baseRoot);
        using IWorldStateScopeProvider.IScope scope = consumer.BeginScope(HeaderAt(baseRoot, 1));
        scope.Get(TestItem.AddressA);
        scope.Get(TestItem.AddressB);
        scope.Get(TestItem.AddressC);
        scope.Get(TestItem.AddressD);
        scope.CreateStorageTree(TestItem.AddressA).Get(SlotA1.Index);
        scope.CreateStorageTree(TestItem.AddressC).Get(SlotC5.Index);
        return (caches, consumer);
    }

    private static Hash256 CommitThroughConsumer(
        PrewarmerScopeProvider consumer,
        Hash256 baseRoot,
        Action<IWorldStateScopeProvider.IWorldStateWriteBatch> writes,
        Action<IWorldStateScopeProvider.IScope> execute = null)
    {
        using IWorldStateScopeProvider.IScope scope = consumer.BeginScope(HeaderAt(baseRoot, 1));
        execute?.Invoke(scope);
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(2))
        {
            writes(writeBatch);
        }

        scope.Commit(2);
        return scope.RootHash;
    }

    private static Account CachedAccount(PreBlockCaches caches, Address address)
    {
        AddressAsKey key = address;
        Assert.That(caches.StateCache.TryGetValue(in key, out Account account), Is.True, $"{address} is cached");
        return account;
    }

    [Test]
    public void Test_ConsumerCommit_ReplaysWritesIntoCarriedCaches()
    {
        using Context ctx = new(useFlat);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, PrewarmerScopeProvider consumer) = WarmConsumerCaches(ctx, baseRoot);

        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, writeBatch =>
        {
            writeBatch.Set(TestItem.AddressA, new Account(2, 400));
            writeBatch.Set(TestItem.AddressB, null);
            using IWorldStateScopeProvider.IStorageWriteBatch storageA = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1);
            storageA.Set(SlotA1.Index, [7]);
        });

        bool carried = caches.PrepareFor(newRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True);
            Assert.That(CachedAccount(caches, TestItem.AddressA)!.Balance, Is.EqualTo((UInt256)400));
            Assert.That(CachedAccount(caches, TestItem.AddressB), Is.Null, "a deleted account is cached as absent");
            Assert.That(CachedAccount(caches, TestItem.AddressC)!.Balance, Is.EqualTo((UInt256)300), "untouched entries survive");
            Assert.That(caches.StorageCache.TryGetValue(in SlotA1, out byte[] slot), Is.True);
            Assert.That(slot, Is.EqualTo(new byte[] { 7 }));
        }

        // The cached account seeds the next scope's storage lookups, so its storage root must be the committed one.
        using IWorldStateScopeProvider.IScope next = consumer.BeginScope(HeaderAt(newRoot, 2));
        Assert.That(next.Get(TestItem.AddressA)!.Balance, Is.EqualTo((UInt256)400));
        Assert.That(next.CreateStorageTree(TestItem.AddressA).Get(SlotA1.Index), Is.EqualTo(new byte[] { 7 }));
    }

    [Test]
    public void Test_StorageOnlyChange_ReplaysAccountWithItsNewStorageRoot()
    {
        using Context ctx = new(useFlat);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, PrewarmerScopeProvider consumer) = WarmConsumerCaches(ctx, baseRoot);

        // No account write: the account only reaches the write set through the storage-root callback.
        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, writeBatch =>
        {
            using IWorldStateScopeProvider.IStorageWriteBatch storageA = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1);
            storageA.Set(SlotA1.Index, [7]);
        });

        bool carried = caches.PrepareFor(newRoot);

        using IWorldStateScopeProvider.IScope next = consumer.BeginScope(HeaderAt(newRoot, 2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True);
            Assert.That(CachedAccount(caches, TestItem.AddressA).StorageRoot, Is.EqualTo(next.CreateStorageTree(TestItem.AddressA).RootHash));
            Assert.That(next.CreateStorageTree(TestItem.AddressA).Get(SlotA1.Index), Is.EqualTo(new byte[] { 7 }));
        }
    }

    [TestCase(true, TestName = "Test_PrepareFor_SameParentAgain_KeepsCachesWithoutTheSealedWrites")]
    [TestCase(false, TestName = "Test_PrepareFor_UnrelatedRoot_ClearsCarriedCaches")]
    public void Test_PrepareFor_AfterACommitLeadingElsewhere(bool sameParent)
    {
        using Context ctx = new(useFlat);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, PrewarmerScopeProvider consumer) = WarmConsumerCaches(ctx, baseRoot);
        CommitThroughConsumer(consumer, baseRoot, writeBatch => writeBatch.Set(TestItem.AddressA, new Account(2, 400)));

        // A sibling block on the same parent still finds the parent's state in the caches; another root does not.
        bool carried = caches.PrepareFor(sameParent ? baseRoot : TestItem.KeccakB);

        AddressAsKey keyA = TestItem.AddressA;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.EqualTo(sameParent));
            Assert.That(caches.StorageCache.TryGetValue(in SlotC5, out _), Is.EqualTo(sameParent));
            Assert.That(caches.StateCache.TryGetValue(in keyA, out Account account), Is.EqualTo(sameParent));
            if (sameParent) Assert.That(account.Balance, Is.EqualTo((UInt256)100), "the sealed writes must not leak into the parent's state");
        }
    }

    [TestCase(true, TestName = "Test_StorageWipe_OfExistingStorage_DropsStorageCache")]
    [TestCase(false, TestName = "Test_StorageWipe_OfNewAccount_KeepsStorageCache")]
    public void Test_StorageWipe(bool preExistingStorage)
    {
        using Context ctx = new(useFlat);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, PrewarmerScopeProvider consumer) = WarmConsumerCaches(ctx, baseRoot);
        Address wiped = preExistingStorage ? TestItem.AddressA : TestItem.AddressD;
        StorageCell written = new(wiped, 9);

        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, writeBatch =>
        {
            writeBatch.Set(wiped, new Account(2, 400));
            using IWorldStateScopeProvider.IStorageWriteBatch storage = writeBatch.CreateStorageWriteBatch(wiped, 1);
            storage.Clear();
            storage.Set(written.Index, [9]);
        });

        bool carried = caches.PrepareFor(newRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True);
            Assert.That(CachedAccount(caches, wiped)!.Balance, Is.EqualTo((UInt256)400));
            Assert.That(caches.StorageCache.TryGetValue(in SlotC5, out _), Is.EqualTo(!preExistingStorage),
                "unrelated slots survive only when the wiped account had no storage to begin with");
            Assert.That(caches.StorageCache.TryGetValue(in written, out byte[] slot), Is.True);
            Assert.That(slot, Is.EqualTo(new byte[] { 9 }));
        }
    }

    [Test]
    public void Test_DeletedAccount_DropsItsCachedSlots()
    {
        using Context ctx = new(useFlat);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, PrewarmerScopeProvider consumer) = WarmConsumerCaches(ctx, baseRoot);

        // Removal without a storage write batch: the contract makes the storage clear implicit.
        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, writeBatch => writeBatch.Set(TestItem.AddressA, null));

        bool carried = caches.PrepareFor(newRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True);
            Assert.That(CachedAccount(caches, TestItem.AddressA), Is.Null);
            Assert.That(caches.StorageCache.TryGetValue(in SlotA1, out _), Is.False, "the removed account's slots must not survive it");
        }
    }

    [TestCase(false, TestName = "Test_DeletedStoragelessAccount_KeepsOtherSlots")]
    [TestCase(true, TestName = "Test_DeletedStoragelessAccount_WithEvictedPreBlockAccount_DropsTheStorageCache")]
    public void Test_DeletedStoragelessAccount(bool preBlockAccountEvicted)
    {
        using Context ctx = new(useFlat);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, PrewarmerScopeProvider consumer) = WarmConsumerCaches(ctx, baseRoot);
        // Without the pre-block account nothing says whether the removed account had slots, so all storage must go.
        if (preBlockAccountEvicted) caches.StateCache.Clear();

        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, writeBatch => writeBatch.Set(TestItem.AddressB, null));

        bool carried = caches.PrepareFor(newRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True);
            Assert.That(CachedAccount(caches, TestItem.AddressB), Is.Null);
            Assert.That(caches.StorageCache.TryGetValue(in SlotC5, out _), Is.EqualTo(!preBlockAccountEvicted));
        }
    }

    [Test]
    public void Test_StorageWipe_SupersedesSlotsWrittenEarlierInTheBlock()
    {
        using Context ctx = new(useFlat);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, PrewarmerScopeProvider consumer) = WarmConsumerCaches(ctx, baseRoot);
        StorageCell slotD1 = new(TestItem.AddressD, 1);

        // Two flushes in one block, as pre-Byzantium per-transaction root commits produce: a new account's slot is
        // written, then its storage is wiped. Nothing pre-block is affected, so only that slot must disappear.
        Hash256 newRoot;
        using (IWorldStateScopeProvider.IScope scope = consumer.BeginScope(HeaderAt(baseRoot, 1)))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch first = scope.StartWriteBatch(1))
            {
                first.Set(TestItem.AddressD, new Account(1, 1));
                using IWorldStateScopeProvider.IStorageWriteBatch storageD = first.CreateStorageWriteBatch(TestItem.AddressD, 1);
                storageD.Clear();
                storageD.Set(slotD1.Index, [7]);
            }

            using (IWorldStateScopeProvider.IWorldStateWriteBatch second = scope.StartWriteBatch(1))
            {
                second.Set(TestItem.AddressD, new Account(2, 2));
                using IWorldStateScopeProvider.IStorageWriteBatch storageD = second.CreateStorageWriteBatch(TestItem.AddressD, 1);
                storageD.Clear();
            }

            scope.Commit(2);
            newRoot = scope.RootHash;
        }

        bool carried = caches.PrepareFor(newRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True);
            Assert.That(caches.StorageCache.TryGetValue(in slotD1, out _), Is.False, "a slot written before the wipe must not be replayed");
            Assert.That(caches.StorageCache.TryGetValue(in SlotC5, out _), Is.True, "wiping an account created in the block leaves pre-block slots alone");
            Assert.That(CachedAccount(caches, TestItem.AddressD).Balance, Is.EqualTo((UInt256)2));
        }
    }

    [TestCase(true, TestName = "Test_StorageWipe_FirstStorageAccessRecordsTheFact_ExistingStorageDropsCache")]
    [TestCase(false, TestName = "Test_StorageWipe_FirstStorageAccessRecordsTheFact_NewAccountKeepsCache")]
    public void Test_StorageWipe_WithEvictedPreBlockAccount(bool preExistingStorage)
    {
        using Context ctx = new(useFlat);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, PrewarmerScopeProvider consumer) = WarmConsumerCaches(ctx, baseRoot);
        Address wiped = preExistingStorage ? TestItem.AddressA : TestItem.AddressD;
        StorageCell written = new(wiped, 9);
        // Every pre-block account is gone from the cache; the answer must come from the block's own storage access.
        caches.StateCache.Clear();

        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot,
            writeBatch =>
            {
                writeBatch.Set(wiped, new Account(2, 400));
                using IWorldStateScopeProvider.IStorageWriteBatch storage = writeBatch.CreateStorageWriteBatch(wiped, 1);
                storage.Clear();
                storage.Set(written.Index, [9]);
            },
            execute: scope => scope.CreateStorageTree(wiped));

        bool carried = caches.PrepareFor(newRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True);
            Assert.That(caches.StorageCache.TryGetValue(in SlotC5, out _), Is.EqualTo(!preExistingStorage),
                "unrelated slots survive only when the wiped account had no storage to begin with");
            Assert.That(caches.StorageCache.TryGetValue(in written, out byte[] slot), Is.True);
            Assert.That(slot, Is.EqualTo(new byte[] { 9 }));
        }
    }

    [Test]
    public void Test_StorageWrittenForAnAbsentAccount_IsNotReplayed()
    {
        using Context ctx = new(useFlat);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, PrewarmerScopeProvider consumer) = WarmConsumerCaches(ctx, baseRoot);
        StorageCell slotD1 = new(TestItem.AddressD, 1);

        // The backends drop or orphan storage of an account that does not exist at the end of the block. The unrelated
        // account change moves the root, so the write set is actually replayed rather than skipped as moot.
        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, writeBatch =>
        {
            writeBatch.Set(TestItem.AddressB, new Account(2, 500));
            using IWorldStateScopeProvider.IStorageWriteBatch storageD = writeBatch.CreateStorageWriteBatch(TestItem.AddressD, 1);
            storageD.Set(slotD1.Index, [7]);
        });
        Assert.That(newRoot, Is.Not.EqualTo(baseRoot), "precondition");

        bool carried = caches.PrepareFor(newRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True);
            Assert.That(CachedAccount(caches, TestItem.AddressB).Balance, Is.EqualTo((UInt256)500), "the write set was replayed");
            Assert.That(caches.StorageCache.TryGetValue(in slotD1, out _), Is.False, "unreachable storage must not be cached");
            Assert.That(caches.StorageCache.TryGetValue(in SlotC5, out _), Is.True, "an account that never had storage triggers no clear");
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
    public void Test_DiscardedScope_WritesAreNotReplayed()
    {
        using Context ctx = new(useFlat);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, PrewarmerScopeProvider consumer) = WarmConsumerCaches(ctx, baseRoot);

        // A block that flushed writes but was thrown away before commit, as on a failed or retried block.
        using (IWorldStateScopeProvider.IScope discarded = consumer.BeginScope(HeaderAt(baseRoot, 1)))
        {
            using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = discarded.StartWriteBatch(1);
            writeBatch.Set(TestItem.AddressA, new Account(9, 999));
        }

        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, writeBatch => writeBatch.Set(TestItem.AddressB, new Account(2, 500)));

        bool carried = caches.PrepareFor(newRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True);
            Assert.That(CachedAccount(caches, TestItem.AddressA)!.Balance, Is.EqualTo((UInt256)100));
            Assert.That(CachedAccount(caches, TestItem.AddressB)!.Balance, Is.EqualTo((UInt256)500));
        }
    }

    [Test]
    public void Test_ConsumerScope_AtAnotherState_ClearsStaleCaches()
    {
        using Context ctx = new(useFlat);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, PrewarmerScopeProvider consumer) = WarmConsumerCaches(ctx, baseRoot);
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

        using IWorldStateScopeProvider.IScope reader = consumer.BeginScope(HeaderAt(otherRoot, 2));

        AddressAsKey keyA = TestItem.AddressA;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(caches.StateCache.TryGetValue(in keyA, out _), Is.False, "entries of another state must not be read");
            Assert.That(caches.ValidFor, Is.Null, "only the driver may vouch for the caches once populators are joined");
            Assert.That(reader.Get(TestItem.AddressC)!.Balance, Is.EqualTo((UInt256)5));
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
