// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtCarryForwardCachingPersistenceTests
{
    private static readonly StateId Basis0 = new(0, Keccak.EmptyTreeHash);
    private static readonly StateId Basis1 = new(1, Keccak.EmptyTreeHash);
    private static readonly ValueHash256 AddressHash = PbtKeyDerivation.AddressKeyHash(TestItem.AddressA);
    private static readonly PbtStorageTreeKey Run1 = PbtStateKey.StorageRun(TestItem.AddressA, AddressHash, 1, out _);
    private static readonly PbtStorageTreeKey Run2 = PbtStateKey.StorageRun(TestItem.AddressA, AddressHash, 100, out _);

    [TestCaseSource(nameof(ReadCases))]
    public void SecondReadAfterScenario_ReadsInnerExpectedTimes(Action<PbtCarryForwardCachingPersistence, FakePersistence> scenario, int expectedAccountReads, int expectedRunReads)
    {
        FakePersistence inner = new();
        PbtCarryForwardCachingPersistence cache = new(inner);

        Read(cache);
        scenario(cache, inner);
        Read(cache);

        Assert.Multiple(() =>
        {
            Assert.That(inner.AccountReads, Is.EqualTo(expectedAccountReads));
            Assert.That(inner.RunReads, Is.EqualTo(expectedRunReads));
        });
    }

    [Test]
    public void GetSlotRun_ServesCallerOwnedCopies()
    {
        PbtCarryForwardCachingPersistence cache = new(new FakePersistence());
        using IPbtPersistence.IReader reader = cache.CreateReader();

        ISlotRun first = reader.GetSlotRun(Run1);
        ISlotRun second = reader.GetSlotRun(Run1);

        Assert.That(second, Is.Not.SameAs(first));
        Assert.That(second.Mask, Is.EqualTo(first.Mask));
    }

    [Test]
    public void GetAccount_WhenCapacityExceeded_EvictsAllThenReCaches()
    {
        FakePersistence inner = new();
        PbtCarryForwardCachingPersistence cache = new(inner, maxEntriesPerKind: 1);
        ValueHash256 other = PbtKeyDerivation.AddressKeyHash(TestItem.AddressB);

        ReadAccount(cache, AddressHash);
        ReadAccount(cache, AddressHash);
        ReadAccount(cache, other);
        ReadAccount(cache, AddressHash);

        Assert.That(inner.AccountReads, Is.EqualTo(3), "second distinct address overflows capacity 1, clearing the first");
    }

    [Test]
    public async Task Module_RegistersDecoratorOnlyWhenEnabled([Values] bool carryForwardCache)
    {
        PbtConfig config = new() { Enabled = true, CarryForwardCache = carryForwardCache };
        await using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(config))
            .AddModule(new PbtModule(config))
            .Build();

        Assert.That(container.Resolve<IPbtPersistence>(), carryForwardCache ? Is.TypeOf<PbtCarryForwardCachingPersistence>() : Is.TypeOf<PbtCachedReaderPersistence>());
    }

    private static IEnumerable<TestCaseData> ReadCases()
    {
        yield return new TestCaseData((Action<PbtCarryForwardCachingPersistence, FakePersistence>)((_, _) => { }), 1, 1)
        { TestName = "same_basis_served_from_cache" };

        yield return Scenario("unwritten_entries_carried_forward", 1, 1, batch =>
        {
            batch.SetAccount(PbtKeyDerivation.AddressKeyHash(TestItem.AddressB), new Account(1, 100));
            batch.SetSlotRun(Run2, SlotRun.Empty);
        });
        yield return Scenario("written_account_invalidated", 2, 1, batch => batch.SetAccount(AddressHash, new Account(1, 100)));
        yield return Scenario("written_run_invalidated", 1, 2, batch => batch.SetSlotRun(Run1, SlotRun.Empty));
        yield return Scenario("clear_storage_clears_cache", 2, 2, batch => batch.ClearStorage(PbtKeyDerivation.AddressKeyHash(TestItem.AddressB)));

        yield return new TestCaseData((Action<PbtCarryForwardCachingPersistence, FakePersistence>)((cache, _) =>
        {
            using IPbtPersistence.IWriteBatch batch = cache.CreateStagingWriteBatch(WriteFlags.None);
            batch.Commit();
        }), 2, 2)
        { TestName = "staging_commit_clears_cache" };

        yield return new TestCaseData((Action<PbtCarryForwardCachingPersistence, FakePersistence>)((cache, inner) =>
        {
            // Writes behind the persistence moved the reader's state; the cache must follow it.
            inner.ReaderState = Basis1;
            cache.ClearCaches();
        }), 2, 2)
        { TestName = "clear_caches_clears_cache_and_follows_reader" };

        yield return new TestCaseData((Action<PbtCarryForwardCachingPersistence, FakePersistence>)((cache, _) =>
        {
            using IPbtPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1, Keccak.EmptyTreeHash, WriteFlags.None);
            batch.SetAccount(AddressHash, new Account(1, 100));
            batch.SetSlotRun(Run1, SlotRun.Empty);
        }), 1, 1)
        { TestName = "uncommitted_batch_does_not_invalidate" };

        yield return new TestCaseData((Action<PbtCarryForwardCachingPersistence, FakePersistence>)((cache, _) =>
        {
            // Advance the cache basis but leave the reader behind, so it must bypass the cache.
            using IPbtPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1, Keccak.EmptyTreeHash, WriteFlags.None);
            batch.Commit();
        }), 2, 2)
        { TestName = "reader_behind_basis_bypasses" };
    }

    private static TestCaseData Scenario(string name, int expectedAccountReads, int expectedRunReads, Action<IPbtPersistence.IWriteBatch> write) =>
        new((Action<PbtCarryForwardCachingPersistence, FakePersistence>)((cache, inner) =>
        {
            using (IPbtPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1, Keccak.EmptyTreeHash, WriteFlags.None))
            {
                write(batch);
                batch.Commit();
            }
            inner.ReaderState = Basis1;
        }), expectedAccountReads, expectedRunReads)
        { TestName = name };

    private static void Read(IPbtPersistence persistence)
    {
        using IPbtPersistence.IReader reader = persistence.CreateReader();
        reader.GetAccount(AddressHash);
        reader.GetSlotRun(Run1);
    }

    private static void ReadAccount(IPbtPersistence persistence, in ValueHash256 addressHash)
    {
        using IPbtPersistence.IReader reader = persistence.CreateReader();
        reader.GetAccount(addressHash);
    }

    public sealed class FakePersistence : IPbtPersistence
    {
        public StateId ReaderState = Basis0;
        public int AccountReads;
        public int RunReads;

        public IPbtPersistence.IReader CreateReader() => new Reader(this);
        public IPbtPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, in ValueHash256 treeRoot, WriteFlags flags) => new WriteBatch();
        public IPbtPersistence.IWriteBatch CreateStagingWriteBatch(WriteFlags flags) => new WriteBatch();
        public void Flush() { }

        private sealed class Reader(FakePersistence parent) : IPbtPersistence.IReader
        {
            public StateId CurrentState => parent.ReaderState;
            public ValueHash256 CurrentRoot => Keccak.EmptyTreeHash;

            public Account? GetAccount(in ValueHash256 addressHash)
            {
                parent.AccountReads++;
                return new Account(1, 100);
            }

            public ISlotRun GetSlotRun(in PbtStorageTreeKey runKey)
            {
                parent.RunReads++;
                Span<EvmWord> values = stackalloc EvmWord[SlotRun.Width];
                values[0] = EvmWordSlot.FromStripped([0x11]);
                return SlotRun.Create(1, values);
            }

            public CodeInfo? GetCode(in ValueHash256 codeHash) => null;
            public IPbtIterator<KeyValuePair<ValueHash256, Account>> EnumerateAccounts() => throw new NotSupportedException();
            public IPbtIterator<KeyValuePair<PbtStorageTreeKey, EvmWord>> EnumerateStorage(ValueHash256? addressHash = null) => throw new NotSupportedException();
            public RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath> => null;
            public IPbtIterator<PbtStorageNodePath> EnumerateNodeGroupKeys() => throw new NotSupportedException();
            public void Dispose() { }
        }

        private sealed class WriteBatch : IPbtPersistence.IWriteBatch
        {
            public void SetAccount(in ValueHash256 addressHash, Account? account) { }
            public void SetSlotRun(in PbtStorageTreeKey runKey, ISlotRun run) { }
            public void SetCode(in ValueHash256 codeHash, CodeInfo code) { }
            public void ClearStorage(in ValueHash256 addressHash) { }
            public void SetNodeGroup<TPath>(TPath groupKey, RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath> { }
            public void Commit() { }
            public void Dispose() { }
        }
    }
}
