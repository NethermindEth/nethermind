// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Reflection;
using System.Runtime.CompilerServices;
using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
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

        public Context(bool useFlat, IStateHeaderProvider stateHeaderProvider, TestMemDb kv = null, TestMemDb codeKv = null)
        {
            if (useFlat)
            {
                (ScopeProvider, _container) = TestWorldStateFactory.CreateFlatScopeProvider(stateHeaderProvider);
            }
            else
            {
                Kv = kv ?? new TestMemDb();
                CodeKv = codeKv ?? new TestMemDb();
                ScopeProvider = new TrieStoreScopeProvider(new TestRawTrieStore(Kv), CodeKv, stateHeaderProvider, LimboLogs.Instance);
            }
        }

        public void Dispose() => _container?.Dispose();
    }

    [Test]
    public void TargetScope_UsesParentState()
    {
        TestStateHeaderProvider stateHeaderProvider = new();
        using Context ctx = new(useFlat, stateHeaderProvider: stateHeaderProvider);
        Hash256 stateRoot = CommitBaseState(ctx);
        stateHeaderProvider.Parent = HeaderAt(stateRoot, 1);
        BlockHeader target = Build.A.BlockHeader
            .WithParent(stateHeaderProvider.Parent)
            .WithStateRoot(TestItem.KeccakA)
            .WithTimestamp(12345)
            .TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctx.ScopeProvider.HasStateForTargetBlock(target), Is.True);
            Assert.That(ctx.ScopeProvider.TryBeginScopeAtTarget(target, new LocalMetrics(), out IWorldStateScopeProvider.IScope scope), Is.True);
            Assert.That(scope, Is.Not.Null);
            Assert.That(scope!.Get(TestItem.AddressA), Is.Not.Null);
            scope.Dispose();
        }
    }

    [Test]
    public void TargetScope_CanBeReopenedAfterDisposal()
    {
        TestStateHeaderProvider stateHeaderProvider = new();
        using Context ctx = new(useFlat, stateHeaderProvider: stateHeaderProvider);
        Hash256 stateRoot = CommitBaseState(ctx);
        stateHeaderProvider.Parent = HeaderAt(stateRoot, 1);
        BlockHeader target = Build.A.BlockHeader.WithParent(stateHeaderProvider.Parent).WithTimestamp(24680).TestObject;

        Assert.That(ctx.ScopeProvider.TryBeginScopeAtTarget(target, new LocalMetrics(), out IWorldStateScopeProvider.IScope firstScope), Is.True);
        firstScope!.Dispose();

        Assert.That(ctx.ScopeProvider.TryBeginScopeAtTarget(target, new LocalMetrics(), out IWorldStateScopeProvider.IScope secondScope), Is.True);
        secondScope!.Dispose();
    }

    [Test]
    public void TargetScope_ReturnsFalseForUnknownParentAndAllowsRetry()
    {
        TestStateHeaderProvider stateHeaderProvider = new();
        using Context ctx = new(useFlat, stateHeaderProvider: stateHeaderProvider);
        BlockHeader parent = HeaderAt(Keccak.EmptyTreeHash, 1);
        BlockHeader target = Build.A.BlockHeader.WithParent(parent).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctx.ScopeProvider.HasStateForTargetBlock(target), Is.False);
            Assert.That(ctx.ScopeProvider.TryBeginScopeAtTarget(target, new LocalMetrics(), out IWorldStateScopeProvider.IScope scope), Is.False);
            Assert.That(scope, Is.Null);
        }

        stateHeaderProvider.Parent = parent;
        Assert.That(ctx.ScopeProvider.TryBeginScopeAtTarget(target, new LocalMetrics(), out IWorldStateScopeProvider.IScope retriedScope), Is.True);
        retriedScope!.Dispose();
    }

    [Test]
    public void TargetScope_ReturnsFalseWhenParentStateRootIsMissing()
    {
        BlockHeader parent = HeaderAt(TestItem.KeccakA, 1);
        using Context ctx = new(useFlat, stateHeaderProvider: new TestStateHeaderProvider { Parent = parent });
        BlockHeader target = Build.A.BlockHeader.WithParent(parent).WithTimestamp(54321).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctx.ScopeProvider.HasStateForTargetBlock(target), Is.False);
            Assert.That(ctx.ScopeProvider.TryBeginScopeAtTarget(target, new LocalMetrics(), out IWorldStateScopeProvider.IScope scope), Is.False);
            Assert.That(scope, Is.Null);
        }
    }

    [Test]
    public void TargetScope_GenesisUsesPreGenesisState()
    {
        TestStateHeaderProvider stateHeaderProvider = new() { ThrowOnLookup = true };
        using Context ctx = new(useFlat, stateHeaderProvider: stateHeaderProvider);
        BlockHeader target = Build.A.BlockHeader.WithNumber(0).WithStateRoot(TestItem.KeccakA).TestObject;

        // ThrowOnLookup is the assertion: a genesis target that consulted the block tree would fail right here.
        Assert.That(ctx.ScopeProvider.TryBeginScopeAtTarget(target, new LocalMetrics(), out IWorldStateScopeProvider.IScope scope), Is.True);
        scope!.Dispose();
    }

    [Test]
    public void DecoratedTargetScope_ForwardsTargetAndAvailability()
    {
        TargetScopeProvider inner = new();
        IWorldStateScopeProvider decorated = new WorldStateMetricsScopeProvider(
            new WorldStateScopeOperationLogger(inner, LimboLogs.Instance), _ => { });
        BlockHeader firstTarget = Build.A.BlockHeader.WithTimestamp(9876).TestObject;
        BlockHeader secondTarget = Build.A.BlockHeader.WithTimestamp(5432).TestObject;

        Assert.That(decorated.HasStateForTargetBlock(firstTarget), Is.True);
        Assert.That(decorated.TryBeginScopeAtTarget(firstTarget, new LocalMetrics(), out IWorldStateScopeProvider.IScope firstScope), Is.True);
        firstScope!.Dispose();
        Assert.That(decorated.HasStateForTargetBlock(secondTarget), Is.True);
        Assert.That(decorated.TryBeginScopeAtTarget(secondTarget, new LocalMetrics(), out IWorldStateScopeProvider.IScope secondScope), Is.True);
        secondScope!.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inner.Targets[0], Is.SameAs(firstTarget));
            Assert.That(inner.Targets[1], Is.SameAs(firstTarget));
            Assert.That(inner.Targets[2], Is.SameAs(secondTarget));
            Assert.That(inner.Targets[3], Is.SameAs(secondTarget));
        }
    }

    [Test]
    public void PrewarmerTargetScope_ForwardsTargetAndAvailability()
    {
        TargetScopeProvider inner = new();
        PrewarmerScopeProvider decorated = new(inner, new PrewarmerState(NewCaches(), isPrewarmer: true), LimboLogs.Instance);
        BlockHeader target = Build.A.BlockHeader.WithTimestamp(6543).TestObject;

        Assert.That(decorated.HasStateForTargetBlock(target), Is.True);
        Assert.That(decorated.TryBeginScopeAtTarget(target, new LocalMetrics(), out IWorldStateScopeProvider.IScope scope), Is.True);
        scope!.Dispose();

        Assert.That(inner.LastTarget, Is.SameAs(target));
        Assert.That(inner.LastMetrics, Is.Not.Null);
    }

    [Test]
    public void WorldStateTargetScope_FailedAcquisitionLeavesScopeReusableAndProtectsNestedScope([Values] bool decorated)
    {
        TargetScopeProvider provider = new() { TryResult = false };
        IWorldState state = decorated
            ? new TargetWorldStateDecorator(new WorldState(provider, LimboLogs.Instance))
            : new WorldState(provider, LimboLogs.Instance);
        BlockHeader target = Build.A.BlockHeader.WithTimestamp(1).TestObject;

        Assert.That(state.HasStateForTargetBlock(target), Is.False);
        Assert.That(state.TryBeginScopeAtTarget(target, out IDisposable failedScope), Is.False);
        Assert.That(provider.LastTarget, Is.SameAs(target), "the refused open still forwarded its own header");
        Assert.That(failedScope, Is.Null);
        Assert.That(state.IsInScope, Is.False);

        provider.TryResult = true;
        BlockHeader reopened = Build.A.BlockHeader.WithTimestamp(2).TestObject;
        Assert.That(state.HasStateForTargetBlock(reopened), Is.True);
        Assert.That(state.TryBeginScopeAtTarget(reopened, out IDisposable scope), Is.True);
        Assert.That(provider.LastTarget, Is.SameAs(reopened), "the accepted open forwarded its own header");
        Assert.That(state.IsInScope, Is.True);
        Assert.That(() => state.TryBeginScopeAtTarget(reopened, out _), Throws.InvalidOperationException);
        Assert.That(state.IsInScope, Is.True);
        scope!.Dispose();
        Assert.That(state.IsInScope, Is.False);
    }

    [Test]
    public void Test_CanSaveToState([Values(1, 4, 8, 9)] int count)
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Address[] addresses = new Address[count];
        addresses[0] = TestItem.AddressA;
        Random random = new(2941 + count);
        for (int i = 1; i < count; i++)
        {
            byte[] bytes = new byte[Address.Size];
            random.NextBytes(bytes);
            addresses[i] = new Address(bytes);
        }

        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            Assert.That(scope.Get(TestItem.AddressA), Is.EqualTo(null));
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(count))
            {
                for (int i = 0; i < count; i++) writeBatch.Set(addresses[i], new Account(100, (UInt256)(100 + i)));
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        Assert.That(stateRoot, Is.Not.EqualTo(Keccak.EmptyTreeHash));
        if (!useFlat && count == 1) Assert.That(ctx.Kv.WritesCount, Is.EqualTo(1));

        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            for (int i = 0; i < count; i++) Assert.That(scope.Get(addresses[i]).Balance, Is.EqualTo((UInt256)(100 + i)));
        }
    }

    [Test]
    [NonParallelizable]
    public void Account_write_batch_preserves_hashes_and_balances([Values(3, 4, 7, 8, 9)] int count, [Values] bool warm)
    {
        ConfigProvider config = new();
        config.GetConfig<IFlatDbConfig>().Enabled = useFlat;
        using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule(config)).Build();
        IWorldStateScopeProvider provider = container.Resolve<IWorldStateManager>().GlobalWorldState;
        using IWorldStateScopeProvider.IScope scope = provider.BeginScope(null);
        Address[] addresses = new Address[count];
        StateTree expected = new();
        Random random = new(9213);
        for (int i = 0; i < count; i++)
        {
            byte[] bytes = new byte[Address.Size];
            do
            {
                random.NextBytes(bytes);
            } while (KeccakCache.TryGet(bytes, out _));
            addresses[i] = new Address(bytes);
            if (warm) KeccakCache.ComputeTo(bytes, out _);
            expected.Set(ValueKeccak.Compute(bytes), new Account(1, (UInt256)(i + 1)));
        }

        using (IWorldStateScopeProvider.IWorldStateWriteBatch write = scope.StartWriteBatch(count))
        {
            for (int i = 0; i < count; i++) write.Set(addresses[i], new Account(1, (UInt256)(i + 1)));
        }

        for (int i = 0; i < count; i++)
        {
            Assert.That(scope.Get(addresses[i]).Balance, Is.EqualTo((UInt256)(i + 1)));
        }

        // The root is the oracle for the batched key hashes: a wrong hash files an account under a
        // different path. Cache residency is not, because a write only stores on an uncontended slot.
        expected.UpdateRootHash();
        scope.Commit(1);
        Assert.That(scope.RootHash, Is.EqualTo(expected.RootHash));
    }

    [Test]
    public void Test_CanSaveToStorage(
        [Values(1, TrieStoreScopeProvider.StorageTreeBulkWriteBatch.MIN_ENTRIES_TO_BATCH + 1)] int estimatedEntries,
        [Values(1, 3, 32)] int valueLength,
        [Values(1UL, 1023UL, 1024UL, ulong.MaxValue)] ulong index, [Values] bool delete)
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);

        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            Assert.That(scope.Get(TestItem.AddressA), Is.EqualTo(null));

            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));

                using IWorldStateScopeProvider.IStorageWriteBatch storageSet = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, estimatedEntries);
                Span<byte> value = stackalloc byte[valueLength];
                value.Fill(0xff);
                storageSet.Set(index, new UInt256(value, isBigEndian: true));
                value.Clear();
            }

            if (delete)
            {
                using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1);
                using IWorldStateScopeProvider.IStorageWriteBatch storageSet = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, estimatedEntries);
                storageSet.Set(index, UInt256.Zero);
            }
            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        Assert.That(stateRoot, Is.Not.EqualTo(Keccak.EmptyTreeHash));
        if (!useFlat) Assert.That(ctx.Kv.WritesCount, Is.EqualTo(delete ? 1 : 2));

        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            IWorldStateScopeProvider.IStorageTree storage = scope.CreateStorageTree(TestItem.AddressA);
            byte[] expected = new byte[delete ? 1 : valueLength];
            if (!delete) expected.AsSpan().Fill(0xff);
            storage.Get(index, out UInt256 slotRead124);
            Assert.That(slotRead124.ToMinimalBigEndian(), Is.EqualTo(expected));
            if (delete) Assert.That(storage.RootHash, Is.EqualTo(Keccak.EmptyTreeHash));
        }
    }

    [Test]
    [NonParallelizable]
    public void Batched_storage_keys_match_scalar_hashes(
        [Values(4, 5, 7, 8, 9, 16, 17, 33)] int count, [Values] bool includeLookupSlots)
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        using IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null);
        UInt256[] indices = new UInt256[count];
        Random random = new(6513 + count);
        byte[] bytes = new byte[Hash256.Size];
        for (int i = 0; i < count; i++)
        {
            do
            {
                random.NextBytes(bytes);
            } while (KeccakCache.TryGet(bytes, out _));
            indices[i] = includeLookupSlots && i % 3 == 0 ? (UInt256)i : new UInt256(bytes, isBigEndian: true);
        }
        for (int round = 0; round < 2; round++)
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch write = scope.StartWriteBatch(2))
            {
                if (round == 0)
                {
                    write.Set(TestItem.AddressA, new Account(100, 100));
                    write.Set(TestItem.AddressB, new Account(100, 100));
                }

                // The same slots down both paths: over MIN_ENTRIES_TO_BATCH the write batch hashes the
                // keys in a batch, at one entry it hashes each on its own.
                using IWorldStateScopeProvider.IStorageWriteBatch batched = write.CreateStorageWriteBatch(TestItem.AddressA, Math.Max(17, count));
                using IWorldStateScopeProvider.IStorageWriteBatch scalar = write.CreateStorageWriteBatch(TestItem.AddressB, 1);
                for (int i = 0; i < count; i++)
                {
                    UInt256 value = round == 1 && i % 2 == 0 ? UInt256.Zero : (UInt256)(i + 1);
                    // Batched first: the scalar Set caches the hash, and a cached key skips the batch.
                    // Round 0 warms every key, so only round 0 reaches the batch kernel.
                    batched.Set(indices[i], value);
                    scalar.Set(indices[i], value);
                }
            }

            // A storage trie is keyed by slot hash alone, so the two addresses hold the same root only
            // if every batched key hash matches the scalar one.
            Assert.That(scope.CreateStorageTree(TestItem.AddressA).RootHash,
                Is.EqualTo(scope.CreateStorageTree(TestItem.AddressB).RootHash), "storage root");

            IWorldStateScopeProvider.IStorageTree tree = scope.CreateStorageTree(TestItem.AddressA);
            for (int i = 0; i < count; i++)
            {
                tree.Get(indices[i], out UInt256 value);
                Assert.That(value, Is.EqualTo(round == 1 && i % 2 == 0 ? UInt256.Zero : (UInt256)(i + 1)), "slot value");
            }
        }
    }

    [Test]
    public void Test_CanSaveToCode()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);

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
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        using IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null);

        // Simulates the EIP-161 scenario: storage is flushed for an account that was
        // then deleted (set to null) during state commit. The write batch Dispose should
        // skip the storage root update for the deleted account instead of throwing.
        using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1);
        using (IWorldStateScopeProvider.IStorageWriteBatch storageSet = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1))
        {
            storageSet.Set(1, new UInt256([1, 2, 3], isBigEndian: true));
        }

        writeBatch.Set(TestItem.AddressA, null);
    }

    [Test]
    public void Test_HintBalWithSink_MatchesIndividualReads()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);

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
                    storageA.Set(1, new UInt256([10, 20], isBigEndian: true));
                    storageA.Set(2, new UInt256([30, 40], isBigEndian: true));
                }

                using IWorldStateScopeProvider.IStorageWriteBatch storageB = writeBatch.CreateStorageWriteBatch(TestItem.AddressB, 1);
                storageB.Set(5, new UInt256([50, 60], isBigEndian: true));
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
                storageTreeA.Get(1, out UInt256 slotRead228);
                Assert.That(sink.Storage[cellA1], Is.EqualTo(slotRead228.ToMinimalBigEndian()));

                Assert.That(sink.Storage.ContainsKey(cellA2), Is.True);
                storageTreeA.Get(2, out UInt256 slotRead231);
                Assert.That(sink.Storage[cellA2], Is.EqualTo(slotRead231.ToMinimalBigEndian()));

                Assert.That(sink.Storage.ContainsKey(cellB5), Is.True);
                storageTreeB.Get(5, out UInt256 slotRead234);
                Assert.That(sink.Storage[cellB5], Is.EqualTo(slotRead234.ToMinimalBigEndian()));
            }
        }
    }

    [Test]
    public void Test_HintBalWithSink_BulkSlotReads_MatchesIndividualReads([Values(10, 1500)] int slotCount)
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);

        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));

                using IWorldStateScopeProvider.IStorageWriteBatch storageA = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, slotCount);
                for (int i = 1; i <= slotCount; i++)
                {
                    storageA.Set((UInt256)i, new UInt256([(byte)i, (byte)(i >> 8)], isBigEndian: true));
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
                storageTreeA.Get((UInt256)i, out UInt256 slotRead279);
                Assert.That(sink.Storage[cell], Is.EqualTo(slotRead279.ToMinimalBigEndian()), $"slot {i}");
            }
        }
    }

    [Test]
    public void Test_HintBal_DoesNotThrow()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);

        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(2))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
                writeBatch.Set(TestItem.AddressB, new Account(200, 200));

                using IWorldStateScopeProvider.IStorageWriteBatch storageA = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1);
                storageA.Set(1, new UInt256([10, 20], isBigEndian: true));
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
    private static readonly StorageCell SlotE1 = new(TestItem.AddressE, 1);

    private static PreBlockCaches NewCaches() => new(TestPreBlockCachesConfig.Small);

    private static BlockHeader HeaderAt(Hash256 stateRoot, ulong number) =>
        Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(number).TestObject;

    // A: balance 100 with slot 1 = [10, 20]; B: balance 200 without storage; C: balance 300 with slot 5 = [5];
    // E: empty by EIP-161 (nonce 0, balance 0, no code) yet holding slot 1 = [3].
    private static Hash256 CommitBaseState(Context ctx)
    {
        using IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null);
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(3))
        {
            writeBatch.Set(TestItem.AddressA, new Account(1, 100));
            writeBatch.Set(TestItem.AddressB, new Account(1, 200));
            writeBatch.Set(TestItem.AddressC, new Account(1, 300));
            writeBatch.Set(TestItem.AddressE, new Account(0, 0));
            using IWorldStateScopeProvider.IStorageWriteBatch storageA = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1);
            storageA.Set(SlotA1.Index, new UInt256([10, 20], isBigEndian: true));
            using IWorldStateScopeProvider.IStorageWriteBatch storageC = writeBatch.CreateStorageWriteBatch(TestItem.AddressC, 1);
            storageC.Set(SlotC5.Index, new UInt256([5], isBigEndian: true));
            using IWorldStateScopeProvider.IStorageWriteBatch storageE = writeBatch.CreateStorageWriteBatch(TestItem.AddressE, 1);
            storageE.Set(SlotE1.Index, new UInt256([3], isBigEndian: true));
        }

        scope.Commit(1);
        return scope.RootHash;
    }

    /// <summary>
    /// Models the driver: caches prepared for the base state, then a block-processing world state over a consumer
    /// scope that reads everything into them.
    /// </summary>
    private static (PreBlockCaches Caches, WorldState Consumer) WarmConsumerCaches(Context ctx, Hash256 baseRoot, PreBlockCachesConfig config = null, ILogManager logManager = null)
    {
        PreBlockCaches caches = config is null ? NewCaches() : new PreBlockCaches(config);
        PrewarmerScopeProvider consumerProvider = new(ctx.ScopeProvider, new PrewarmerState(caches, isPrewarmer: false), logManager ?? LimboLogs.Instance);
        WorldState consumer = new(consumerProvider, LimboLogs.Instance);
        caches.PrepareFor(baseRoot);
        using (consumer.BeginScope(HeaderAt(baseRoot, 1)))
        {
            consumer.GetBalance(TestItem.AddressA);
            consumer.GetBalance(TestItem.AddressB);
            consumer.GetBalance(TestItem.AddressC);
            consumer.AccountExists(TestItem.AddressD);
            consumer.Get(in SlotA1, out _);
            consumer.Get(in SlotC5, out _);
            consumer.GetBalance(TestItem.AddressE);
            consumer.Get(in SlotE1, out _);
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

    /// <summary>Selfdestruct of <see cref="TestItem.AddressA"/> as the transaction processor commits it.</summary>
    private static readonly Action<WorldState> DestroyA = static ws =>
    {
        ws.GetBalance(TestItem.AddressA);
        ws.MarkStorageDestroyed(TestItem.AddressA);
        ws.DeleteAccount(TestItem.AddressA);
    };

    private static Account CachedAccount(PreBlockCaches caches, Address address)
    {
        AddressAsKey key = address;
        Assert.That(caches.StateCache.TryGetValue(in key, out Account account), Is.True, $"{address} is cached");
        return account;
    }

    /// <summary>Whether a storage read of <paramref name="cell"/> would be answered from the storage cache.</summary>
    private static bool ServedFromCache(PreBlockCaches caches, in StorageCell cell) =>
        !caches.BypassesStorageCache(cell.Address) && caches.StorageCache.TryGetValue(in cell, out _);

    private static byte[] CachedSlot(PreBlockCaches caches, in StorageCell cell)
    {
        Assert.That(caches.StorageCache.TryGetValue(in cell, out UInt256 value), Is.True, $"{cell} is cached");
        return value.ToMinimalBigEndian();
    }

    [Test]
    public void Test_ConsumerCommit_WritesTheBlocksFinalValuesBackIntoTheCarriedCaches()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);

        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, ws =>
        {
            ws.AddToBalance(TestItem.AddressA, 300, Cancun.Instance, out _);
            ws.DeleteAccount(TestItem.AddressB);
            ws.Set(in SlotA1, (UInt256)7);
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
            consumer.Get(in SlotA1, out UInt256 storageValue1);
            Assert.That(storageValue1.ToMinimalBigEndian(), Is.EqualTo(new byte[] { 7 }));
        }
    }

    [Test]
    public void Test_StorageOnlyChange_CachesTheAccountWithItsNewStorageRoot()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);
        Hash256 baseStorageRoot = CachedAccount(caches, TestItem.AddressA).StorageRoot;

        // No account-level change: the account's new storage root only exists once the storage tree is committed.
        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, ws => ws.Set(in SlotA1, (UInt256)7));

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
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);
        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, ws => ws.AddToBalance(TestItem.AddressA, 300, Cancun.Instance, out _));
        TestLogger testLogger = new();

        // The commit moved the caches on: a sibling block on the parent finds nothing it can use.
        Hash256 requestedRoot = committedRoot ? newRoot : baseRoot;
        bool carried = caches.PrepareFor(requestedRoot, new ILogger(testLogger));

        AddressAsKey keyA = TestItem.AddressA;
        string[] expectedLogs = committedRoot
            ? []
            : [$"Pre-block caches cleared because cached state root {newRoot} does not match requested state root {baseRoot}"];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.EqualTo(committedRoot));
            Assert.That(caches.StateCache.TryGetValue(in keyA, out Account account), Is.EqualTo(committedRoot));
            Assert.That(caches.StorageCache.TryGetValue(in SlotC5, out _), Is.EqualTo(committedRoot));
            Assert.That(testLogger.LogList, Is.EqualTo(expectedLogs));
            if (committedRoot) Assert.That(account.Balance, Is.EqualTo((UInt256)400));
        }
    }

    [TestCase(true, TestName = "Test_StorageClear_OfPreBlockStorage_BypassesOnlyThatContract")]
    [TestCase(false, TestName = "Test_StorageClear_OfAnAccountWithoutStorage_KeepsTheStorageCache")]
    public void Test_StorageClear(bool preExistingStorage)
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
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
            ws.Set(in written, (UInt256)9);
        });

        bool carried = caches.PrepareFor(newRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True);
            Assert.That(caches.BypassesStorageCache(cleared), Is.EqualTo(preExistingStorage),
                "only a contract that held storage before the block has pre-block slots to go stale");
            Assert.That(CachedSlot(caches, in SlotC5), Is.EqualTo(new byte[] { 5 }), "unrelated slots survive the clear");
            Assert.That(ServedFromCache(caches, in SlotA1), Is.EqualTo(!preExistingStorage),
                "the cleared account's pre-block slots must not be served after the clear");
            Assert.That(caches.StorageCache.TryGetValue(in written, out _), Is.EqualTo(!preExistingStorage),
                "a bypassed contract's slots are not written back");
            if (!preExistingStorage) Assert.That(CachedSlot(caches, in written), Is.EqualTo(new byte[] { 9 }));
        }

        // The next block reads the cleared contract from the committed state, not from what the cache held before.
        using (consumer.BeginScope(HeaderAt(newRoot, 2)))
        using (Assert.EnterMultipleScope())
        {
            consumer.Get(in SlotA1, out UInt256 slotA1);
            consumer.Get(in written, out UInt256 writtenValue);
            consumer.Get(in SlotC5, out UInt256 slotC5);
            Assert.That(slotA1, Is.EqualTo(preExistingStorage ? UInt256.Zero : new UInt256([10, 20], isBigEndian: true)));
            Assert.That(writtenValue, Is.EqualTo((UInt256)9));
            Assert.That(slotC5, Is.EqualTo((UInt256)5));
        }
    }

    [Test]
    public void Test_DestroyedAccount_DropsItsCachedSlots()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
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
            Assert.That(ServedFromCache(caches, in SlotA1), Is.False, "the removed account's slots must not survive it");
            Assert.That(CachedSlot(caches, in SlotC5), Is.EqualTo(new byte[] { 5 }), "nor may the removal take other contracts' slots");
        }
    }

    [TestCase(true, TestName = "Test_AccountDestroyedAcrossTwoFlushes_WithPreBlockStorage_BypassesOnlyThatContract")]
    [TestCase(false, TestName = "Test_AccountDestroyedAcrossTwoFlushes_CreatedInTheBlock_LeavesNoTrace")]
    public void Test_AccountDestroyedAcrossTwoFlushes(bool preExistingStorage)
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);
        Address destroyed = preExistingStorage ? TestItem.AddressA : TestItem.AddressD;
        StorageCell written = new(destroyed, 9);

        // Per-transaction root commits, as pre-Byzantium receipts require: the slot reaches the state in the first flush,
        // which moves the storage root, and the account is destroyed before the second.
        Hash256 newRoot;
        using (consumer.BeginScope(HeaderAt(baseRoot, 1)))
        {
            if (!preExistingStorage) consumer.CreateAccount(destroyed, 1);
            consumer.Set(in written, (UInt256)7);
            consumer.Commit(Cancun.Instance);
            consumer.GetBalance(destroyed);
            consumer.MarkStorageDestroyed(destroyed);
            consumer.DeleteAccount(destroyed);
            consumer.Commit(Cancun.Instance);
            consumer.CommitTree(2);
            newRoot = consumer.StateRoot;
        }

        bool carried = caches.PrepareFor(newRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True);
            Assert.That(CachedAccount(caches, destroyed), Is.Null);
            Assert.That(ServedFromCache(caches, in written), Is.False, "a slot of the destroyed account must not be cached");
            Assert.That(ServedFromCache(caches, in SlotA1), Is.EqualTo(!preExistingStorage), "pre-block slots of the destroyed account must not survive it");
            Assert.That(caches.BypassesStorageCache(destroyed), Is.EqualTo(preExistingStorage));
            Assert.That(CachedSlot(caches, in SlotC5), Is.EqualTo(new byte[] { 5 }), "unrelated slots survive either way");
        }
    }

    [Test]
    public void Test_StorageWrittenForAnAccountThatEndsAbsent_IsNotCached()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);
        StorageCell slotD1 = new(TestItem.AddressD, 1);

        // Looked up as a creation's collision check does, then created, written and removed without a storage clear.
        // Its slot never became state, so a later contract at the address must not find it in the caches.
        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, ws =>
        {
            ws.AccountExists(TestItem.AddressD);
            ws.CreateAccount(TestItem.AddressD, 1);
            ws.Set(in slotD1, (UInt256)7);
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
    public void Test_StorageWrittenWithoutTheAccountEverLoaded_BypassesThatContract()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);
        StorageCell slotD1 = new(TestItem.AddressD, 1);

        // Execution loads an account before writing its storage; a write without that load leaves the caches unable to
        // tell the account's fate, so they must drop every slot rather than keep pre-block ones that may be stale.
        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, ws =>
        {
            ws.CreateAccount(TestItem.AddressD, 1);
            ws.Set(in slotD1, (UInt256)7);
            ws.DeleteAccount(TestItem.AddressD);
            ws.AddToBalance(TestItem.AddressB, 300, Cancun.Instance, out _);
        });

        bool carried = caches.PrepareFor(newRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True);
            Assert.That(ServedFromCache(caches, in slotD1), Is.False, "storage that never became state must not be cached");
            Assert.That(caches.BypassesStorageCache(TestItem.AddressD), Is.True, "an unknown fate bypasses the contract's storage");
            Assert.That(CachedSlot(caches, in SlotC5), Is.EqualTo(new byte[] { 5 }), "and leaves every other contract's alone");
            Assert.That(CachedAccount(caches, TestItem.AddressB).Balance, Is.EqualTo((UInt256)500), "the account cache is unaffected");
        }
    }

    [Test]
    public void Test_DirectStorageRead_OfAnAccountTheBlockNeverLoaded_ClearsNothing()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);

        // System reads go straight to storage, so the block ends with a storage record for C but no account record.
        // That is not a removal: C keeps its storage, and nothing may be cleared for it.
        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, ws =>
        {
            ws.Get(in SlotC5, out _);
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
    public void Test_RevertedStorageClear_LeavesNoStaleCachedValue()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);

        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, ws =>
        {
            ws.Get(in SlotA1, out _);
            Snapshot snapshot = ws.TakeSnapshot();
            ws.ClearStorage(TestItem.AddressA);
            ws.Restore(snapshot);
            ws.Set(in SlotA1, (UInt256)7);
        });

        bool carried = caches.PrepareFor(newRoot);

        using (consumer.BeginScope(HeaderAt(newRoot, 2)))
        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True);
            Assert.That(CachedSlot(caches, in SlotA1), Is.EqualTo(new byte[] { 7 }));
            Assert.That(CachedSlot(caches, in SlotC5), Is.EqualTo(new byte[] { 5 }));
            consumer.Get(in SlotA1, out UInt256 storageValue2);
            Assert.That(storageValue2.ToMinimalBigEndian(), Is.EqualTo(new byte[] { 7 }));
            Assert.That(CachedAccount(caches, TestItem.AddressA).StorageRoot, Is.Not.EqualTo(Keccak.EmptyTreeHash), "the account keeps its storage");
        }
    }

    [Test]
    public void Test_SlotSetToZero_IsCachedAsZero()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);

        // A zero write is a delete for the tree, but a read of the slot must still be served as zero, not as a miss.
        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, ws => ws.Set(in SlotA1, (UInt256)0));

        bool carried = caches.PrepareFor(newRoot);

        using (consumer.BeginScope(HeaderAt(newRoot, 2)))
        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True);
            Assert.That(CachedSlot(caches, in SlotA1).IsZero(), Is.True);
            consumer.Get(in SlotA1, out UInt256 storageValue3);
            Assert.That(storageValue3.IsZero, Is.True);
        }
    }

    [Test]
    public void Test_TouchedEmptyAccountWithStorage_IsRemovedWithItsCachedSlots()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);
        StorageCell slotE2 = new(TestItem.AddressE, 2);

        // A touch removes the EIP-161-empty account, storage included, with no storage clear on the way. The reverted
        // write leaves a storage record that never resolved the tree, so it cannot vouch for the storage either.
        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, ws =>
        {
            Snapshot snapshot = ws.TakeSnapshot();
            ws.Set(in slotE2, (UInt256)1);
            ws.Restore(snapshot);
            ws.AddToBalance(TestItem.AddressE, UInt256.Zero, Cancun.Instance, out _);
        });

        bool carried = caches.PrepareFor(newRoot);

        using (consumer.BeginScope(HeaderAt(newRoot, 2)))
        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried, Is.True);
            Assert.That(CachedAccount(caches, TestItem.AddressE), Is.Null);
            Assert.That(ServedFromCache(caches, in SlotE1), Is.False, "the removed account's slots must not survive it");
            consumer.Get(in SlotE1, out UInt256 storageValue4);
            Assert.That(storageValue4.IsZero, Is.True, "the state agrees the storage is gone");
        }
    }

    [Test]
    public void Test_StorageWipe_KeepsTheRestOfTheBlocksWriteBack()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);
        TestLogger testLogger = new() { IsDebug = false };
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot, logManager: new OneLoggerLogManager(new ILogger(testLogger)));

        // A wipe used to drop the whole storage cache and end the write-back there, taking the block's other slots with it.
        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, ws =>
        {
            DestroyA(ws);
            ws.GetBalance(TestItem.AddressC);
            ws.Set(in SlotC5, (UInt256)9);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(caches.PrepareFor(newRoot), Is.True);
            Assert.That(caches.BypassesStorageCache(TestItem.AddressA), Is.True);
            Assert.That(caches.StorageBypassCount, Is.EqualTo(1));
            Assert.That(ServedFromCache(caches, in SlotA1), Is.False);
            Assert.That(CachedSlot(caches, in SlotC5), Is.EqualTo(new byte[] { 9 }), "the block's other slots are written back");
            Assert.That(CachedSlot(caches, in SlotE1), Is.EqualTo(new byte[] { 3 }), "and slots it did not touch are kept");
            Assert.That(testLogger.LogList, Is.Empty, "a wipe that loses nothing else is not worth an Info line");
        }
    }

    [Test]
    public void Test_WipedContract_RecreatedInALaterBlock_IsReadFromTheState()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);
        StorageCell slotA2 = new(TestItem.AddressA, 2);
        // A slot the cache holds from before the wipe that the state no longer has.
        caches.StorageCache.Set(in slotA2, (UInt256)77);

        Hash256 destroyedRoot = CommitThroughConsumer(consumer, baseRoot, DestroyA);
        Assert.That(caches.PrepareFor(destroyedRoot), Is.True);

        // Recreated at the same address (CREATE2), with a new value in the slot the old contract used.
        Hash256 recreatedRoot;
        using (consumer.BeginScope(HeaderAt(destroyedRoot, 2)))
        {
            Assert.That(consumer.AccountExists(TestItem.AddressA), Is.False);
            consumer.CreateAccount(TestItem.AddressA, 1);
            consumer.Set(in SlotA1, (UInt256)3);
            consumer.Commit(Cancun.Instance);
            consumer.CommitTree(3);
            recreatedRoot = consumer.StateRoot;
        }

        Assert.That(caches.PrepareFor(recreatedRoot), Is.True);
        using (consumer.BeginScope(HeaderAt(recreatedRoot, 3)))
        using (Assert.EnterMultipleScope())
        {
            Assert.That(caches.BypassesStorageCache(TestItem.AddressA), Is.True, "the contract keeps bypassing until the next clear");
            consumer.Get(in SlotA1, out UInt256 slotA1);
            consumer.Get(in slotA2, out UInt256 slotA2Value);
            Assert.That(slotA1, Is.EqualTo((UInt256)3), "the recreated contract's value, not the one cached before the wipe");
            Assert.That(slotA2Value, Is.EqualTo(UInt256.Zero), "a slot the wipe removed reads as empty");
            Assert.That(ServedFromCache(caches, in SlotA1), Is.False);
            Assert.That(ServedFromCache(caches, in slotA2), Is.False);
        }
    }

    [Test]
    public void Test_StorageWipesPastTheCap_ClearTheStorageCacheAndTheBypass()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);
        PreBlockCachesConfig config = TestPreBlockCachesConfig.Small with { MaxStorageWipesBeforeClear = 1 };
        TestLogger testLogger = new() { IsDebug = false };
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot, config, new OneLoggerLogManager(new ILogger(testLogger)));

        Hash256 firstRoot = CommitThroughConsumer(consumer, baseRoot, DestroyA);
        Assert.That(caches.PrepareFor(firstRoot), Is.True);
        Assert.That(caches.StorageBypassCount, Is.EqualTo(1), "precondition: one wipe fits under the cap");

        // A second wipe would pass the cap, so the whole storage cache goes instead, and with it the bypass.
        Hash256 secondRoot;
        using (consumer.BeginScope(HeaderAt(firstRoot, 2)))
        {
            consumer.AddToBalance(TestItem.AddressE, UInt256.Zero, Cancun.Instance, out _);
            consumer.Commit(Cancun.Instance);
            consumer.CommitTree(3);
            secondRoot = consumer.StateRoot;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(caches.PrepareFor(secondRoot), Is.True, "a cleared storage cache still describes the committed state");
            Assert.That(caches.StorageBypassCount, Is.Zero);
            Assert.That(caches.StorageCache.TryGetValue(in SlotC5, out _), Is.False);
            Assert.That(caches.StorageCache.TryGetValue(in SlotA1, out _), Is.False);
            Assert.That(caches.StorageCache.TryGetValue(in SlotE1, out _), Is.False);
            Assert.That(testLogger.LogList, Is.EqualTo(new[] { "Pre-block storage cache cleared after 2 contracts wiped their storage" }));
        }

        using (consumer.BeginScope(HeaderAt(secondRoot, 3)))
        {
            consumer.Get(in SlotA1, out UInt256 slotA1);
            consumer.Get(in SlotE1, out UInt256 slotE1);
            consumer.Get(in SlotC5, out UInt256 slotC5);
            Assert.That((slotA1, slotE1, slotC5), Is.EqualTo((UInt256.Zero, UInt256.Zero, (UInt256)5)));
        }
    }

    [Test]
    public void Test_StateMismatch_ClearsTheBypassWithTheStorageCache()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);
        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, DestroyA);
        Assert.That(caches.PrepareFor(newRoot), Is.True);
        Assert.That(caches.BypassesStorageCache(TestItem.AddressA), Is.True, "precondition");

        // A sibling on the parent: the caches are cleared, and a contract with nothing left in them need not bypass.
        Assert.That(caches.PrepareFor(baseRoot), Is.False);
        using (consumer.BeginScope(HeaderAt(baseRoot, 1)))
        using (Assert.EnterMultipleScope())
        {
            Assert.That(caches.StorageBypassCount, Is.Zero);
            Assert.That(caches.StorageCache.TryGetValue(in SlotA1, out _), Is.False);
            consumer.Get(in SlotA1, out UInt256 slotA1);
            Assert.That(slotA1, Is.EqualTo(new UInt256([10, 20], isBigEndian: true)), "the parent still has the slot");
            Assert.That(CachedSlot(caches, in SlotA1), Is.EqualTo(new byte[] { 10, 20 }), "and caches it again");
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public void Test_WipedContract_IsNeverServedStale_ToConcurrentReaders()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);
        const int slots = 64;
        // Stale entries for every slot the readers will ask for, as a busy contract would leave behind.
        for (int i = 1; i <= slots; i++)
        {
            StorageCell cell = new(TestItem.AddressA, (UInt256)i);
            caches.StorageCache.Set(in cell, (UInt256)(1000 + i));
        }

        Hash256 newRoot = CommitThroughConsumer(consumer, baseRoot, DestroyA);
        Assert.That(caches.PrepareFor(newRoot), Is.True);

        // Populators of the next block: many scopes reading the wiped contract and an unrelated one at once, each
        // backfilling on a miss, which is exactly what could put a pre-wipe value back in front of a reader. The bypass
        // set is fixed by now, as it only changes between blocks, so the readers race each other's backfill, not the set.
        PrewarmerScopeProvider populators = new(ctx.ScopeProvider, new PrewarmerState(caches, isPrewarmer: true), LimboLogs.Instance);
        ConcurrentBag<string> wrong = [];
        // The test trie store is not built for concurrent scope opening; the reads through the caches are what race.
        Lock open = new();
        using Barrier start = new(8);
        Thread[] readers = new Thread[8];
        for (int t = 0; t < readers.Length; t++)
        {
            int seed = t;
            readers[t] = new Thread(() =>
            {
                try
                {
                    start.SignalAndWait();
                    Random random = new(seed);
                    for (int round = 0; round < 200; round++)
                    {
                        IWorldStateScopeProvider.IScope scope;
                        IWorldStateScopeProvider.IStorageTree wiped, kept;
                        lock (open)
                        {
                            scope = populators.BeginScope(HeaderAt(newRoot, 2));
                            wiped = scope.CreateStorageTree(TestItem.AddressA);
                            kept = scope.CreateStorageTree(TestItem.AddressC);
                        }

                        for (int i = 0; i < slots; i++)
                        {
                            UInt256 index = (UInt256)random.Next(1, slots + 1);
                            wiped.Get(in index, out UInt256 value);
                            if (!value.IsZero) wrong.Add($"{TestItem.AddressA}[{index}] = {value}");
                            kept.Get(SlotC5.Index, out UInt256 kept5);
                            if (kept5 != (UInt256)5) wrong.Add($"{TestItem.AddressC}[5] = {kept5}");
                        }

                        lock (open) scope.Dispose();
                    }
                }
                catch (Exception e)
                {
                    wrong.Add(e.ToString());
                }
            });
            readers[t].Start();
        }

        foreach (Thread reader in readers) reader.Join();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(wrong, Is.Empty, "every read of the wiped contract comes from the committed state");
            for (int i = 1; i <= slots; i++)
            {
                StorageCell cell = new(TestItem.AddressA, (UInt256)i);
                Assert.That(ServedFromCache(caches, in cell), Is.False, $"{cell} is never served from the cache");
                caches.StorageCache.TryGetValue(in cell, out UInt256 raw);
                Assert.That(raw, Is.EqualTo((UInt256)(1000 + i)), $"{cell} was not backfilled");
            }
            Assert.That(CachedSlot(caches, in SlotC5), Is.EqualTo(new byte[] { 5 }), "an unrelated contract keeps being cached");
        }
    }

    [Test]
    public void Test_TwoBlocksInOneScope_CarryTheCachesThroughBothCommits()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);

        StorageCell slotD1 = new(TestItem.AddressD, 1);

        // A branch processes several blocks in one scope: each commit must start from the root the previous one left,
        // and a removal the first block made must not be applied again by the second, which touched no storage.
        Hash256 secondRoot;
        using (consumer.BeginScope(HeaderAt(baseRoot, 1)))
        {
            consumer.CreateAccount(TestItem.AddressD, 1);
            consumer.Set(in slotD1, (UInt256)7);
            consumer.GetBalance(TestItem.AddressA);
            consumer.MarkStorageDestroyed(TestItem.AddressA);
            consumer.DeleteAccount(TestItem.AddressA);
            consumer.Commit(Cancun.Instance);
            consumer.CommitTree(2);
            // What the driver does between blocks of a branch, and what joins the first commit's write-back.
            Assert.That(caches.PrepareFor(consumer.StateRoot), Is.True, "the first commit must carry the caches to its own root");

            consumer.AddToBalance(TestItem.AddressB, 300, Cancun.Instance, out _);
            // Cached within the second block, so replaying the first block's removal would clear it away again.
            consumer.Get(in SlotC5, out _);
            consumer.Commit(Cancun.Instance);
            consumer.CommitTree(3);
            secondRoot = consumer.StateRoot;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(caches.PrepareFor(secondRoot), Is.True);
            Assert.That(CachedAccount(caches, TestItem.AddressA), Is.Null);
            Assert.That(CachedAccount(caches, TestItem.AddressB).Balance, Is.EqualTo((UInt256)500));
            Assert.That(CachedSlot(caches, in SlotC5), Is.EqualTo(new byte[] { 5 }), "the second commit must not replay the first block's removal");
        }
    }

    [Test]
    public void Test_DetachedStorageChanges_SurviveTheNextBlock()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);
        PreBlockCaches caches = NewCaches();
        caches.PrepareFor(baseRoot);
        WorldState state = new(ctx.ScopeProvider, LimboLogs.Instance);

        using (state.BeginScope(HeaderAt(baseRoot, 1)))
        {
            // Execution always loads an account before writing its storage; without that the contract's fate is
            // unknown at block end and the write-back clears its slots instead of writing them.
            state.GetBalance(TestItem.AddressA);
            state.Set(in SlotA1, (UInt256)7);
            state.Commit(Cancun.Instance);

            // Taken where CommitTree takes it, once the storage roots are flushed, then held while the next block
            // refills the collections it came from.
            IWorldStateScopeProvider.IBlockChangeSnapshot detached = state._persistentStorageProvider.DetachBlockChanges();

            state.CommitTree(2);
            Hash256 firstRoot = state.StateRoot;

            state.GetBalance(TestItem.AddressC);
            state.Set(in SlotA1, (UInt256)9);
            state.Set(in SlotC5, (UInt256)9);
            state.Commit(Cancun.Instance);
            state.CommitTree(3);

            caches.WriteBackInBackground(baseRoot, firstRoot, () => detached, LimboLogs.Instance.GetClassLogger<PreBlockCaches>());
            caches.PrepareFor(firstRoot);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(CachedSlot(caches, in SlotA1), Is.EqualTo(new byte[] { 7 }), "the snapshot must hold the block it was taken from");
            Assert.That(caches.StorageCache.TryGetValue(in SlotC5, out _), Is.False, "and nothing the next block touched");
        }
    }

    [Test]
    public void Test_SecondBlockInAScope_LeavesTheFirstsDetachedChangesAlone()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);
        (PreBlockCaches caches, WorldState consumer) = WarmConsumerCaches(ctx, baseRoot);

        StorageCell slotA2 = new(TestItem.AddressA, 2);

        // Nothing joins between the two commits. Whether or not the first write-back is still running when the second
        // block starts, both must land: Test_DetachedStorageChanges_SurviveTheNextBlock forces the overlap itself.
        Hash256 secondRoot;
        using (consumer.BeginScope(HeaderAt(baseRoot, 1)))
        {
            consumer.Set(in SlotA1, (UInt256)7);
            consumer.Commit(Cancun.Instance);
            consumer.CommitTree(2);

            consumer.Set(in slotA2, (UInt256)8);
            consumer.AddToBalance(TestItem.AddressB, 300, Cancun.Instance, out _);
            consumer.Commit(Cancun.Instance);
            consumer.CommitTree(3);
            secondRoot = consumer.StateRoot;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(caches.PrepareFor(secondRoot), Is.True, "both write-backs ran to completion");
            Assert.That(CachedSlot(caches, in SlotA1), Is.EqualTo(new byte[] { 7 }));
            Assert.That(CachedSlot(caches, in slotA2), Is.EqualTo(new byte[] { 8 }));
            Assert.That(CachedAccount(caches, TestItem.AddressB).Balance, Is.EqualTo((UInt256)500));
        }
    }

    [Test]
    public void Test_ScopeEndingWithoutACommit_LeavesTheCachesAtTheBaseState()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
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
    public void Test_WriteBack_UnderContention_ClearsTheCachesAndForgetsTheState()
    {
        PreBlockCaches caches = NewCaches();
        AddressAsKey keyA = TestItem.AddressA;
        AddressAsKey keyB = TestItem.AddressB;
        caches.PrepareFor(TestItem.KeccakA);
        caches.StateCache.Set(in keyA, new Account(1, 100));
        caches.StateCache.Set(in keyB, new Account(1, 200));
        // A lock bit left on the entry stands for a writer that got in: the upsert must give up rather than wait.
        LockEntryHolding(caches.StateCache, TestItem.AddressA);

        caches.WriteBackInBackground(
            TestItem.KeccakA,
            TestItem.KeccakC,
            () => new TestSnapshot(writeBack => writeBack.Set(TestItem.AddressA, new Account(2, 400))),
            LimboLogs.Instance.GetClassLogger<PreBlockCaches>());
        caches.EnsureNotStaleFor(TestItem.KeccakC);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(caches.ValidFor, Is.Null, "caches that may be half-updated describe no state");
            Assert.That(caches.StateCache.TryGetValue(in keyB, out _), Is.False, "a write-back that saw contention clears the caches");
        }
    }

    /// <summary>A snapshot whose write the test drives, so it can observe where and when the write-back runs.</summary>
    private sealed class TestSnapshot(
        Action<IWorldStateScopeProvider.IWorldStateWriteBatch> write,
        Exception disposeFailure = null) : IWorldStateScopeProvider.IBlockChangeSnapshot
    {
        public int WriteThreadId { get; private set; }

        public bool Disposed { get; private set; }

        public void WriteTo(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch)
        {
            WriteThreadId = Environment.CurrentManagedThreadId;
            write(writeBatch);
        }

        public void Dispose()
        {
            Disposed = true;
            if (disposeFailure is not null) throw disposeFailure;
        }
    }

    [Test]
    public void Test_WriteBackInBackground_LeavesTheCommitThread_AndIsJoinedBeforeTheCachesAreRead()
    {
        PreBlockCaches caches = NewCaches();
        caches.PrepareFor(TestItem.KeccakA);
        using ManualResetEventSlim writing = new();
        using ManualResetEventSlim release = new();
        TestSnapshot snapshot = new(writeBack =>
        {
            writing.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            writeBack.Set(TestItem.AddressA, new Account(2, 400));
        });

        caches.WriteBackInBackground(TestItem.KeccakA, TestItem.KeccakB, () => snapshot, LimboLogs.Instance.GetClassLogger<PreBlockCaches>());

        Assert.That(writing.Wait(TimeSpan.FromSeconds(30)), Is.True, "the write-back runs without the commit thread driving it");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(caches.ValidFor, Is.EqualTo(TestItem.KeccakA), "the commit thread returns before the write-back lands");
            Assert.That(snapshot.WriteThreadId, Is.Not.EqualTo(Environment.CurrentManagedThreadId));
        }

        release.Set();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(caches.PrepareFor(TestItem.KeccakB), Is.True, "preparing for the next block joins the write-back and keeps what it brought");
            Assert.That(CachedAccount(caches, TestItem.AddressA).Balance, Is.EqualTo((UInt256)400));
            Assert.That(snapshot.Disposed, Is.True, "the block's collections go back once written");
        }
    }

    [Test]
    public void Test_WriteBackInBackground_ThatFaults_DropsTheCachesInsteadOfFailingTheBlock()
    {
        PreBlockCaches caches = NewCaches();
        AddressAsKey key = TestItem.AddressA;
        caches.PrepareFor(TestItem.KeccakA);
        caches.StateCache.Set(in key, new Account(1, 100));
        TestSnapshot snapshot = new(_ => throw new InvalidOperationException("half way"));

        // Nothing on the block's path waits for the write-back, so its failure must not reach the block.
        Assert.DoesNotThrow(() => caches.WriteBackInBackground(TestItem.KeccakA, TestItem.KeccakB, () => snapshot, LimboLogs.Instance.GetClassLogger<PreBlockCaches>()));

        caches.EnsureNotStaleFor(TestItem.KeccakB);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(caches.ValidFor, Is.Null, "a write-back that did not finish vouches for nothing");
            Assert.That(caches.StateCache.TryGetValue(in key, out _), Is.False);
            Assert.That(snapshot.Disposed, Is.True, "a failed write-back still releases the block's collections");
        }
    }

    [Test]
    public void Test_WriteBackInBackground_ThatFailsToRelease_StillDoesNotFailTheNextBlock()
    {
        PreBlockCaches caches = NewCaches();
        caches.PrepareFor(TestItem.KeccakA);
        TestSnapshot snapshot = new(_ => { }, disposeFailure: new InvalidOperationException("release failed"));

        caches.WriteBackInBackground(TestItem.KeccakA, TestItem.KeccakB, () => snapshot, LimboLogs.Instance.GetClassLogger<PreBlockCaches>());

        // The join is the next block's first act; a write-back that could not let go must not reach it.
        Assert.DoesNotThrow(() => caches.PrepareFor(TestItem.KeccakB));
        Assert.DoesNotThrow(() => caches.PrepareFor(TestItem.KeccakB), "nor any block after it");
    }

    [Test]
    public void Test_WriteBackInBackground_TakesNoSnapshotWhenTheCachesDescribeAnotherState()
    {
        PreBlockCaches caches = NewCaches();
        caches.PrepareFor(TestItem.KeccakA);

        bool taken = false;
        caches.WriteBackInBackground(TestItem.KeccakB, TestItem.KeccakC, () =>
        {
            taken = true;
            return new TestSnapshot(_ => { });
        }, LimboLogs.Instance.GetClassLogger<PreBlockCaches>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(taken, Is.False, "a block on another state must not pay to snapshot changes that would be dropped");
            Assert.That(caches.ValidFor, Is.EqualTo(TestItem.KeccakA));
        }
    }

    private static void LockEntryHolding(SeqlockCache<AddressAsKey, Account> cache, Address address)
    {
        Array entries = (Array)typeof(SeqlockCache<AddressAsKey, Account>)
            .GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)
            .GetValue(cache);
        AddressAsKey key = address;
        for (int i = 0; i < entries.Length; i++)
        {
            object entry = entries.GetValue(i);
            if (!key.Equals((AddressAsKey)entry.GetType().GetField("Key").GetValue(entry))) continue;

            FieldInfo header = entry.GetType().GetField("HashEpochSeqLock");
            header.SetValue(entry, (long)header.GetValue(entry) | long.MinValue);
            entries.SetValue(entry, i);
            return;
        }

        Assert.Fail($"{address} is not cached");
    }

    [Test]
    public void Test_UnchangedRoot_WritesNothingBack()
    {
        PreBlockCaches caches = NewCaches();
        AddressAsKey key = TestItem.AddressA;
        caches.PrepareFor(TestItem.KeccakA);
        caches.StateCache.Set(in key, new Account(1, 100));
        // A scope that computes no roots, as a trieless one does, reports its base root after a commit.
        IWorldStateScopeProvider.IScope baseScope = Substitute.For<IWorldStateScopeProvider.IScope>();
        baseScope.RootHash.Returns(TestItem.KeccakA);
        IWorldStateScopeProvider baseProvider = Substitute.For<IWorldStateScopeProvider>();
        baseProvider.TryBeginScope(Arg.Any<BlockHeader>(), Arg.Any<LocalMetrics>(), out Arg.Any<IWorldStateScopeProvider.IScope>()).Returns(call => call.Succeed(2, baseScope));
        PrewarmerScopeProvider consumer = new(baseProvider, new PrewarmerState(caches, isPrewarmer: false), LimboLogs.Instance);

        bool ran = false;
        using (IWorldStateScopeProvider.IScope scope = consumer.BeginScope(HeaderAt(TestItem.KeccakA, 1)))
        {
            scope.Commit(2);
            scope.WriteBackCommittedState(() =>
            {
                ran = true;
                return Substitute.For<IWorldStateScopeProvider.IBlockChangeSnapshot>();
            });
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ran, Is.False, "post-block values must not be filed under the pre-block root, nor a snapshot taken to file them");
            Assert.That(CachedAccount(caches, TestItem.AddressA).Balance, Is.EqualTo((UInt256)100));
            Assert.That(caches.ValidFor, Is.EqualTo(TestItem.KeccakA));
        }
    }

    [Test]
    public void Test_ConsumerScope_OpeningFailure_LeavesNothingBehind()
    {
        PreBlockCaches caches = NewCaches();
        caches.ConsumerScopeOpened += () => throw new InvalidOperationException("join failed");
        IWorldStateScopeProvider.IScope baseScope = Substitute.For<IWorldStateScopeProvider.IScope>();
        IWorldStateScopeProvider baseProvider = Substitute.For<IWorldStateScopeProvider>();
        baseProvider.TryBeginScope(Arg.Any<BlockHeader>(), Arg.Any<LocalMetrics>(), out Arg.Any<IWorldStateScopeProvider.IScope>()).Returns(call => call.Succeed(2, baseScope));
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
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
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

    /// <remarks>
    /// Main processing opens the consumer through <see cref="IWorldStateScopeProvider.TryBeginScopeAtTarget"/>, so the
    /// stale-cache check has to fire on the state the target's parent names, not on the target itself.
    /// </remarks>
    [Test]
    public void Test_ConsumerTargetScope_KeepsCachesOfItsParentAndClearsAnotherState([Values] bool parentIsTheWarmedState)
    {
        TestStateHeaderProvider stateHeaderProvider = new();
        using Context ctx = new(useFlat, stateHeaderProvider);
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

        BlockHeader parent = stateHeaderProvider.Add(HeaderAt(parentIsTheWarmedState ? baseRoot : otherRoot, parentIsTheWarmedState ? 1u : 2u));
        BlockHeader target = Build.A.BlockHeader.WithParent(parent).TestObject;

        AddressAsKey keyA = TestItem.AddressA;
        using (consumer.BeginScopeAtTarget(target))
        {
            Assert.That(caches.StateCache.TryGetValue(in keyA, out _), Is.EqualTo(parentIsTheWarmedState),
                parentIsTheWarmedState
                    ? "caches warmed for the target's own parent must be kept"
                    : "entries of another state must not be read");
        }
    }

    [Test]
    public void Test_ConsumerScope_StaysOpenUntilTheUnderlyingScopeIsDisposed()
    {
        PreBlockCaches caches = NewCaches();
        IWorldStateScopeProvider.IScope baseScope = Substitute.For<IWorldStateScopeProvider.IScope>();
        bool openDuringBaseDispose = false;
        baseScope.When(s => s.Dispose()).Do(_ => openDuringBaseDispose = caches.ConsumerScopeOpen);
        IWorldStateScopeProvider baseProvider = Substitute.For<IWorldStateScopeProvider>();
        baseProvider.TryBeginScope(Arg.Any<BlockHeader>(), Arg.Any<LocalMetrics>(), out Arg.Any<IWorldStateScopeProvider.IScope>()).Returns(call => call.Succeed(2, baseScope));
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
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);

        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
                using IWorldStateScopeProvider.IStorageWriteBatch storageA = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1);
                storageA.Set(1, new UInt256([10, 20], isBigEndian: true));
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        // isPrewarmer: false targets the main-processing scope where HintBal actually runs.
        PreBlockCaches caches = NewCaches();
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

    [Test]
    public void Test_MainScope_RegisteredForConsumerScopeLifetime([Values] bool isPrewarmer)
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);

        PreBlockCaches caches = NewCaches();
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
        innerProvider.TryBeginScope(Arg.Any<BlockHeader>(), Arg.Any<LocalMetrics>(), out Arg.Any<IWorldStateScopeProvider.IScope>()).Returns(call => call.Succeed(2, inner));

        IWorldStateScopeProvider decorated = new WorldStateMetricsScopeProvider(
            new WorldStateScopeOperationLogger(innerProvider, LimboLogs.Instance), _ => { });

        PreBlockCaches caches = NewCaches();
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

    /// <summary>
    /// Runs <paramref name="work"/> on a populator world state and returns the consumer scope its hints reached.
    /// </summary>
    private static IWorldStateScopeProvider.IScope RunPopulator(Context ctx, Hash256 baseRoot, Action<WorldState> work)
    {
        PreBlockCaches caches = NewCaches();
        IWorldStateScopeProvider.IScope mainScope = Substitute.For<IWorldStateScopeProvider.IScope>();
        // The wrapper captures it when the scope opens, so it must be in place first.
        caches.MainScope = mainScope;
        PrewarmerScopeProvider populator = new(ctx.ScopeProvider, new PrewarmerState(caches, isPrewarmer: true), LimboLogs.Instance);
        WorldState state = new(populator, LimboLogs.Instance);

        using (state.BeginScope(HeaderAt(baseRoot, 1)))
        {
            work(state);
        }

        return mainScope;
    }

    [Test]
    public void Test_PopulatorAccountRead_WarmsNothing()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);

        // A read leaves the account's leaf alone, so the commit never walks its path.
        IWorldStateScopeProvider.IScope mainScope = RunPopulator(ctx, baseRoot, ws => ws.GetBalance(TestItem.AddressA));

        mainScope.DidNotReceive().HintWarmAccount(Arg.Any<ValueAddress>());
    }

    [Test]
    public void Test_PopulatorAccountWrite_WarmsTheAccount()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);

        IWorldStateScopeProvider.IScope mainScope = RunPopulator(ctx, baseRoot,
            ws => ws.AddToBalance(TestItem.AddressA, 1, Cancun.Instance, out _));

        mainScope.Received(1).HintWarmAccount(new ValueAddress(TestItem.AddressA.Bytes));
    }

    [Test]
    public void Test_PopulatorStorageWrite_WarmsTheSlotAndTheContractsAccount([Values(1, 8)] int repetitions)
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);

        // The storage root lives in the account, so writing a slot rewrites the contract's leaf as well, and
        // once is enough however many of its slots the block writes.
        StorageCell slotA2 = new(TestItem.AddressA, 2);
        IWorldStateScopeProvider.IScope mainScope = RunPopulator(ctx, baseRoot, ws =>
        {
            for (int i = 0; i < repetitions; i++) ws.Set(in SlotA1, (UInt256)(7 + i));
            for (int i = 0; i < repetitions; i++) ws.Set(in slotA2, (UInt256)(8 + i));
        });

        mainScope.Received(1).HintWarmSlot(new ValueAddress(TestItem.AddressA.Bytes), SlotA1.Index);
        mainScope.Received(1).HintWarmSlot(new ValueAddress(TestItem.AddressA.Bytes), slotA2.Index);
        mainScope.Received(1).HintWarmAccount(new ValueAddress(TestItem.AddressA.Bytes));
    }

    [Test]
    public void Test_PopulatorSlotHint_IsRenewedAfterScopeReuse([Values] bool resetTransactionChanges)
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);
        PreBlockCaches caches = NewCaches();
        IWorldStateScopeProvider.IScope mainScope = Substitute.For<IWorldStateScopeProvider.IScope>();
        caches.MainScope = mainScope;
        PrewarmerScopeProvider populator = new(ctx.ScopeProvider, new PrewarmerState(caches, isPrewarmer: true), LimboLogs.Instance);
        WorldState state = new(populator, LimboLogs.Instance);

        for (int round = 0; round < 2; round++)
        {
            using (state.BeginScope(HeaderAt(baseRoot, 1)))
            {
                Snapshot snapshot = state.TakeSnapshot();
                state.Set(in SlotA1, (UInt256)7);
                state.Restore(snapshot);
                if (resetTransactionChanges) state.Reset(resetBlockChanges: false);
                state.Set(in SlotA1, (UInt256)8);
            }
            mainScope.Received(round + 1).HintWarmSlot(new ValueAddress(TestItem.AddressA.Bytes), SlotA1.Index);
        }
    }

    [Test]
    public void Test_PopulatorStorageDestroy_WarmsTheContractsAccount()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);

        // Destroying storage moves the root without writing a slot, so nothing else on the write path hints it.
        IWorldStateScopeProvider.IScope mainScope = RunPopulator(ctx, baseRoot, ws =>
        {
            ws.GetBalance(TestItem.AddressA);
            ws.MarkStorageDestroyed(TestItem.AddressA);
        });

        mainScope.Received(1).HintWarmAccount(new ValueAddress(TestItem.AddressA.Bytes));
    }

    [Test]
    public void Test_PopulatorStorageClear_WarmsTheContractsAccount()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);

        IWorldStateScopeProvider.IScope mainScope = RunPopulator(ctx, baseRoot, ws =>
        {
            ws.GetBalance(TestItem.AddressA);
            ws.ClearStorage(TestItem.AddressA);
        });

        mainScope.Received(1).HintWarmAccount(new ValueAddress(TestItem.AddressA.Bytes));
    }

    [Test]
    public void Test_PopulatorBlock_WarmsWhatTheCommitRewritesAndNothingElse()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);

        IWorldStateScopeProvider.IScope mainScope = RunPopulator(ctx, baseRoot, ws =>
        {
            ws.GetBalance(TestItem.AddressB);
            ws.AddToBalance(TestItem.AddressA, 1, Cancun.Instance, out _);
            ws.Set(in SlotC5, (UInt256)9);
            ws.CreateAccount(TestItem.AddressD, 1);
        });

        // The commit rewrites the leaves of A, C (through its storage root) and D, and leaves B alone.
        mainScope.Received().HintWarmAccount(new ValueAddress(TestItem.AddressA.Bytes));
        mainScope.Received().HintWarmAccount(new ValueAddress(TestItem.AddressC.Bytes));
        mainScope.Received().HintWarmAccount(new ValueAddress(TestItem.AddressD.Bytes));
        mainScope.DidNotReceive().HintWarmAccount(new ValueAddress(TestItem.AddressB.Bytes));
    }

    [Test]
    public void Test_PopulatorStorageRead_WarmsNothing()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);
        Hash256 baseRoot = CommitBaseState(ctx);

        IWorldStateScopeProvider.IScope mainScope = RunPopulator(ctx, baseRoot, ws => ws.Get(in SlotA1, out _));

        mainScope.DidNotReceive().HintWarmSlot(Arg.Any<ValueAddress>(), Arg.Any<UInt256>());
        mainScope.DidNotReceive().HintWarmAccount(Arg.Any<ValueAddress>());
    }

    [Test]
    public void Test_PopulatorHintWarmSlot_RoutesToMainScope()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);

        PreBlockCaches caches = NewCaches();
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
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);

        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
                using IWorldStateScopeProvider.IStorageWriteBatch storage = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1);
                storage.Set(1, new UInt256([1, 2, 3], isBigEndian: true));
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        PreBlockCaches caches = NewCaches();
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
            scope.CreateStorageTree(TestItem.AddressA).Get(1, out _);
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
            storage.Get(1, out _);
            storage.Get(2, out _);
        }

        Assert.That(consumerMetrics.PreBlockAccountHits, Is.EqualTo(1));
        Assert.That(consumerMetrics.PreBlockAccountMisses, Is.EqualTo(1));
        Assert.That(consumerMetrics.PreBlockStorageHits, Is.EqualTo(1));
        Assert.That(consumerMetrics.PreBlockStorageMisses, Is.EqualTo(1));
    }

    [Test]
    public void Test_ZeroStorageCacheEntry_DoesNotReadBackingTree()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);

        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
                using IWorldStateScopeProvider.IStorageWriteBatch storage = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1);
                storage.Set(1, new UInt256([10, 20], isBigEndian: true));
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        PreBlockCaches caches = NewCaches();
        StorageCell cell = new(TestItem.AddressA, 1);
        LocalMetrics metrics = new();
        PrewarmerScopeProvider consumer = new(ctx.ScopeProvider, new PrewarmerState(caches, isPrewarmer: false), LimboLogs.Instance);
        BlockHeader baseBlock = Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject;

        using IWorldStateScopeProvider.IScope readScope = consumer.BeginScope(baseBlock, metrics);
        caches.StorageCache.Set(in cell, UInt256.Zero);
        readScope.CreateStorageTree(TestItem.AddressA).Get(1, out UInt256 slotRead1472);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slotRead1472, Is.EqualTo(UInt256.Zero));
            Assert.That(metrics.PreBlockStorageHits, Is.EqualTo(1));
            Assert.That(metrics.PreBlockStorageMisses, Is.Zero);
        }
    }

    [Test]
    public void Test_PopulatorStorageCapture_SkipsBackingReadWithoutCachingSpeculativeValue()
    {
        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);

        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
                using IWorldStateScopeProvider.IStorageWriteBatch storage = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1);
                storage.Set(1, new UInt256([10, 20], isBigEndian: true));
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        PreBlockCaches caches = NewCaches();
        PrewarmerScopeProvider populator = new(ctx.ScopeProvider, new PrewarmerState(caches, isPrewarmer: true), LimboLogs.Instance);
        BlockHeader baseBlock = Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject;
        StorageCell cell = new(TestItem.AddressA, 1);

        using (PreBlockCaches.StorageReadCapture capture = caches.BeginStorageReadCapture(new StrongBox<int>(16)))
        {
            using IWorldStateScopeProvider.IScope readScope = populator.BeginScope(baseBlock);
            IWorldStateScopeProvider.IStorageTree capturedStorageTree = readScope.CreateStorageTree(TestItem.AddressA);
            capturedStorageTree.Get(1, out UInt256 slotRead1510);
            Assert.That(slotRead1510.ToMinimalBigEndian(), Is.EqualTo(new byte[] { 1 }));
            Assert.That(capture.Cells, Does.Contain(cell));
        }

        Assert.That(caches.StorageCache.TryGetValue(in cell, out _), Is.False);
        using IWorldStateScopeProvider.IScope uncapturedReadScope = populator.BeginScope(baseBlock);
        IWorldStateScopeProvider.IStorageTree uncapturedStorageTree = uncapturedReadScope.CreateStorageTree(TestItem.AddressA);
        uncapturedStorageTree.Get(1, out UInt256 slotRead1517);
        Assert.That(slotRead1517.ToMinimalBigEndian(), Is.EqualTo(new byte[] { 10, 20 }));
        Assert.That(caches.StorageCache.TryGetValue(in cell, out UInt256 cached), Is.True);
        Assert.That(cached, Is.EqualTo(new UInt256([10, 20], isBigEndian: true)));
    }

    [Test]
    public void Test_FlatScope_TrieWarmHints_Smoke()
    {
        Assume.That(useFlat, Is.True);

        using Context ctx = new(useFlat, UnavailableStateHeaderProvider.Instance);

        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(2))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
                writeBatch.Set(TestItem.AddressB, new Account(200, 200));
                using IWorldStateScopeProvider.IStorageWriteBatch storageA = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1);
                storageA.Set(1, new UInt256([10, 20], isBigEndian: true));
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        PreBlockCaches caches = NewCaches();
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

    private sealed class TargetWorldStateDecorator(IWorldState state) : WorldStateDecorator(state);

    private sealed class TargetScopeProvider : IWorldStateScopeProvider
    {
        public bool TryResult { get; set; } = true;
        public List<BlockHeader> Targets { get; } = [];
        public BlockHeader LastTarget { get; private set; }
        public LocalMetrics LastMetrics { get; private set; }

        public bool HasRoot(BlockHeader baseBlock) => true;

        public bool HasStateForTargetBlock(BlockHeader targetBlock)
        {
            LastTarget = targetBlock;
            Targets.Add(targetBlock);
            return TryResult;
        }

        public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, out IWorldStateScopeProvider.IScope scope)
        {
            LastTarget = targetBlock;
            Targets.Add(targetBlock);
            LastMetrics = metrics;
            if (!TryResult)
            {
                scope = null;
                return false;
            }

            scope = Substitute.For<IWorldStateScopeProvider.IScope>();
            return true;
        }

        public bool TryBeginScope(BlockHeader baseBlock, LocalMetrics metrics, out IWorldStateScopeProvider.IScope scope)
        {
            scope = Substitute.For<IWorldStateScopeProvider.IScope>();
            return true;
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

        public void OnStorageRead(in StorageCell storageCell, in UInt256 value)
            => Storage[storageCell] = value.ToMinimalBigEndian();
    }
#nullable disable
}
