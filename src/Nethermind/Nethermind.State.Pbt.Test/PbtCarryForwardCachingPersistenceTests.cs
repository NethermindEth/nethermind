// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtCarryForwardCachingPersistenceTests
{
    private static readonly StateId Basis0 = new(0, Keccak.EmptyTreeHash);
    private static readonly StateId Basis1 = new(1, Keccak.EmptyTreeHash);
    private static readonly ValueHash256 AddressHash = PbtStateKey.AddressKeyHash(TestItem.AddressA);
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

        PackedSlotRun first = reader.GetSlotRun(Run1);
        PackedSlotRun second = reader.GetSlotRun(Run1);

        Assert.That(second, Is.Not.SameAs(first));
        Assert.That(second.Mask, Is.EqualTo(first.Mask));
    }

    [Test]
    public void GetAccount_WhenCapacityExceeded_EvictsAllThenReCaches()
    {
        FakePersistence inner = new();
        PbtCarryForwardCachingPersistence cache = new(inner, maxEntriesPerKind: 1);
        ValueHash256 other = PbtStateKey.AddressKeyHash(TestItem.AddressB);

        ReadAccount(cache, AddressHash);
        ReadAccount(cache, AddressHash);
        ReadAccount(cache, other);
        ReadAccount(cache, AddressHash);

        Assert.That(inner.AccountReads, Is.EqualTo(3), "second distinct address overflows capacity 1, clearing the first");
    }

    /// <summary>
    /// A reader whose generation ends between its currency check and its lookup must not serve the entry a reader at
    /// the next state filled. Every commit writes every account with its block number as the balance, so a stale hit
    /// reads a balance other than its reader's block.
    /// </summary>
    [Test]
    public async Task ConcurrentReadersAndCommitter_ReadEachReaderState()
    {
        const int readerThreads = 16;
        const int readsPerReader = 64;
        ValueHash256[] addressHashes = [.. Enumerable.Range(0, 8).Select(i => PbtStateKey.AddressKeyHash(TestItem.Addresses[i]))];
        FakePersistence inner = new();
        PbtCarryForwardCachingPersistence cache = new(inner);
        using Barrier startLine = new(readerThreads + 1);
        int mismatches = 0;

        Task committer = Task.Factory.StartNew(() =>
        {
            Random random = new(1);
            startLine.SignalAndWait();
            for (ulong block = 1; block <= 30_000; block++)
            {
                using (IPbtPersistence.IWriteBatch batch = cache.CreateWriteBatch(new StateId(block - 1, Keccak.EmptyTreeHash), new StateId(block, Keccak.EmptyTreeHash), Keccak.EmptyTreeHash, WriteFlags.None))
                {
                    foreach (ValueHash256 addressHash in addressHashes) batch.SetAccount(addressHash, new Account(1, block).ToPbtAccount());
                    batch.Commit();
                }
                inner.ReaderState = new StateId(block, Keccak.EmptyTreeHash);
                Thread.SpinWait(random.Next(2000));
            }
        }, TaskCreationOptions.LongRunning);

        Task[] readers = [.. Enumerable.Range(0, readerThreads).Select(seed => Task.Factory.StartNew(() =>
        {
            Random random = new(seed + 2);
            startLine.SignalAndWait();
            do
            {
                using IPbtPersistence.IReader reader = cache.CreateReader();
                for (int i = 0; i < readsPerReader; i++)
                {
                    if (reader.GetAccount(addressHashes[random.Next(addressHashes.Length)])!.Value.ToAccount().Balance != reader.CurrentState.BlockNumber)
                        Interlocked.Increment(ref mismatches);
                }
            } while (!committer.IsCompleted);
        }, TaskCreationOptions.LongRunning))];

        await Task.WhenAll([committer, .. readers]).WaitAsync(TimeSpan.FromMinutes(5));
        Assert.That(mismatches, Is.Zero);
    }

    private static IEnumerable<TestCaseData> ReadCases()
    {
        yield return new TestCaseData((Action<PbtCarryForwardCachingPersistence, FakePersistence>)((_, _) => { }), 1, 1)
        { TestName = "same_basis_served_from_cache" };

        yield return Scenario("unwritten_entries_carried_forward", 1, 1, batch =>
        {
            batch.SetAccount(PbtStateKey.AddressKeyHash(TestItem.AddressB), new Account(1, 100).ToPbtAccount());
            batch.SetSlotRun(Run2, SlotRun.Empty);
        });
        yield return Scenario("written_account_invalidated", 2, 1, batch => batch.SetAccount(AddressHash, new Account(1, 100).ToPbtAccount()));
        yield return Scenario("written_run_invalidated", 1, 2, batch => batch.SetSlotRun(Run1, SlotRun.Empty));

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
            batch.SetAccount(AddressHash, new Account(1, 100).ToPbtAccount());
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
        public void Flush() { }

        private sealed class Reader(FakePersistence parent) : IPbtPersistence.IReader
        {
            public StateId CurrentState { get; } = parent.ReaderState;
            public ValueHash256 CurrentRoot => Keccak.EmptyTreeHash;

            public PbtAccount? GetAccount(in ValueHash256 addressHash)
            {
                parent.AccountReads++;
                return PbtAccount.From(new Account(1, CurrentState.BlockNumber), null);
            }

            public PackedSlotRun GetSlotRun(in PbtStorageTreeKey runKey)
            {
                parent.RunReads++;
                Span<EvmWord> values = stackalloc EvmWord[SlotRun.Width];
                values[0] = EvmWordSlot.FromStripped([0x11]);
                return SlotRun.Create(1, values);
            }

            public CodeInfo? GetCode(in ValueHash256 codeHash) => null;
            public bool TryGetCodeLeaf(in PbtPath key, out ValueHash256 value) => throw new NotSupportedException();
            public IEnumerator<KeyValuePair<ValueHash256, PbtAccount>> EnumerateAccounts() => throw new NotSupportedException();
            public RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath> => null;
            public void Dispose() { }
        }

        private sealed class WriteBatch : IPbtPersistence.IWriteBatch
        {
            public void SetAccount(in ValueHash256 addressHash, PbtAccount? account) { }
            public void SetSlotRun(in PbtStorageTreeKey runKey, PackedSlotRun run) { }
            public void SetCode(in ValueHash256 codeHash, CodeInfo code) { }
            public void SetCodeLeaf(in PbtPath key, in ValueHash256 value) { }
            public void SetNodeGroup<TPath>(TPath groupKey, RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath> { }
            public void Commit() { }
            public void Dispose() { }
        }
    }
}
