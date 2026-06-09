// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
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

                using (IWorldStateScopeProvider.IStorageWriteBatch storageSet = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1))
                {
                    storageSet.Set(1, [1, 2, 3]);
                }
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
            using (IWorldStateScopeProvider.ICodeSetter writer = scope.CodeDb.BeginCodeWrite())
            {
                writer.Set(TestItem.KeccakA, [1, 2, 3]);
            }
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
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            using (IWorldStateScopeProvider.IStorageWriteBatch storageSet = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1))
            {
                storageSet.Set(1, [1, 2, 3]);
            }

            writeBatch.Set(TestItem.AddressA, null);
        }
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

                using (IWorldStateScopeProvider.IStorageWriteBatch storageB = writeBatch.CreateStorageWriteBatch(TestItem.AddressB, 1))
                {
                    storageB.Set(5, [50, 60]);
                }
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

    [Test]
    public void Test_HintBalWithSink_ManySlots_ReadInFullAfterSorting()
    {
        using Context ctx = new(useFlat);

        // 8 slots so the prewarmer's per-account on-disk-order sort runs over a non-trivial run;
        // a dropped/duplicated/misread slot surfaces as a missing or wrong sink value below.
        UInt256[] slots = [7, 3, 1, 8, 2, 6, 4, 5];

        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
                using (IWorldStateScopeProvider.IStorageWriteBatch storageA = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, slots.Length))
                {
                    foreach (UInt256 slot in slots)
                        storageA.Set(slot, [(byte)slot, (byte)(slot + 100)]);
                }
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageReads(slots).TestObject)
            .TestObject;

        CollectingBalSink sink = new();
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            scope.HintBal(bal, sink).Wait();

            IWorldStateScopeProvider.IStorageTree storageTreeA = scope.CreateStorageTree(TestItem.AddressA);
            foreach (UInt256 slot in slots)
            {
                StorageCell cell = new(TestItem.AddressA, slot);
                Assert.That(sink.Storage.ContainsKey(cell), Is.True, $"slot {slot} missing from sink");
                Assert.That(sink.Storage[cell], Is.EqualTo(storageTreeA.Get(slot)), $"slot {slot} value mismatch");
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

                using (IWorldStateScopeProvider.IStorageWriteBatch storageA = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1))
                {
                    storageA.Set(1, [10, 20]);
                }
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
                using (IWorldStateScopeProvider.IStorageWriteBatch storageA = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1))
                {
                    storageA.Set(1, [10, 20]);
                }
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
    public void Test_HintBal_DrainedTask_DoesNotThrow_WhenReHintCancels()
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

        Task hint;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            // Start a warming pass, then immediately re-hint: the second HintBal cancels the first task's
            // token (as the next block does). The first task must still complete - not end Cancelled -
            // because BranchProcessor.WaitAndClear drains it via GetResult on fast/empty blocks and would
            // otherwise surface a TaskCanceledException and fail the block.
            hint = scope.HintBal(bal);
            scope.HintBal(bal);
        }

        Assert.DoesNotThrow(() => hint.GetAwaiter().GetResult());
        Assert.That(hint.IsCanceled, Is.False);
    }

    [Test]
    public void Test_HintBal_CallerTokenCancels_DrainedTask_DoesNotThrow()
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

        using CancellationTokenSource cts = new();
        Task hint;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            // The caller's token (e.g. the block processor's tx-complete background cancel) is linked into
            // the warming; cancelling it must stop the warming without faulting the drained task.
            hint = scope.HintBal(bal, sink: null, token: cts.Token);
            cts.Cancel();
        }

        Assert.DoesNotThrow(() => hint.GetAwaiter().GetResult());
        Assert.That(hint.IsCanceled, Is.False);
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
