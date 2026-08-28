// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
[NonParallelizable]
public class CarryForwardCachingPersistenceTests
{
    private static readonly StateId Basis0 = new(0, Keccak.EmptyTreeHash);
    private static readonly StateId Basis1 = new(1, Keccak.EmptyTreeHash);
    private static readonly StateId Basis2 = new(2, Keccak.EmptyTreeHash);
    private static readonly Address Address = TestItem.AddressA;

    public enum CacheKind
    {
        Account,
        Slot
    }

    [TestCaseSource(nameof(SlotReadCases))]
    public void TryGetSlot_SecondReadAfterScenario_ReadsInnerExpectedTimes(Action<CarryForwardCachingPersistence, FakePersistence> scenario, int expectedSlotReads)
    {
        FakePersistence inner = new();
        CarryForwardCachingPersistence cache = new(inner);

        ReadSlot(cache, 1);
        scenario(cache, inner);
        ReadSlot(cache, 1);

        Assert.That(inner.SlotReads, Is.EqualTo(expectedSlotReads));
    }

    [Test]
    public void GetAccount_SecondReadAtSameBasis_ServedFromCache()
    {
        FakePersistence inner = new();
        CarryForwardCachingPersistence cache = new(inner);

        using (IPersistence.IPersistenceReader reader = cache.CreateReader()) reader.GetAccount(Address);
        using (IPersistence.IPersistenceReader reader = cache.CreateReader()) reader.GetAccount(Address);

        Assert.That(inner.AccountReads, Is.EqualTo(1));
    }

    [Test]
    public void GetAccount_WhenCapacityExceeded_EvictsAllThenReCaches()
    {
        FakePersistence inner = new();
        CarryForwardCachingPersistence cache = new(inner, maxEntriesPerKind: 1);

        ReadAccount(cache, TestItem.AddressA);
        ReadAccount(cache, TestItem.AddressA);
        ReadAccount(cache, TestItem.AddressB);
        ReadAccount(cache, TestItem.AddressA);

        Assert.That(inner.AccountReads, Is.EqualTo(3), "second distinct address overflows capacity 1, clearing the first");
    }

    [Test]
    public void GetAccount_AfterCommit_ServesTheCommittedValue()
    {
        FakePersistence inner = new();
        CarryForwardCachingPersistence cache = new(inner);
        Account committed = Build.An.Account.WithNonce(7).TestObject;

        ReadAccount(cache, Address);
        using (IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1))
            batch.SetAccount(Address, committed);
        inner.ReaderState = Basis1;

        Account? read;
        using (IPersistence.IPersistenceReader reader = cache.CreateReader()) read = reader.GetAccount(Address);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inner.AccountReads, Is.EqualTo(1), "the commit refreshed the entry, so the re-read must not reach the inner store");
            Assert.That(read?.Nonce, Is.EqualTo(committed.Nonce));
        }
    }

    [Test]
    public void GetAccount_AfterCommittedDeletion_ServesAsAbsent()
    {
        FakePersistence inner = new();
        CarryForwardCachingPersistence cache = new(inner);

        ReadAccount(cache, Address);
        using (IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1))
            batch.SetAccount(Address, null);
        inner.ReaderState = Basis1;

        Account? read;
        using (IPersistence.IPersistenceReader reader = cache.CreateReader()) read = reader.GetAccount(Address);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read, Is.Null, "a committed null means the account is gone, and that must be cached as absent");
            Assert.That(inner.AccountReads, Is.EqualTo(1));
        }
    }

    [Test]
    public void GetAccount_WhenCommitWouldOverflowCapacity_DoesNotEvictResidentEntries()
    {
        FakePersistence inner = new();
        CarryForwardCachingPersistence cache = new(inner, maxEntriesPerKind: 1);

        ReadAccount(cache, TestItem.AddressA);
        using (IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1))
            batch.SetAccount(TestItem.AddressB, Build.An.Account.WithNonce(1).TestObject);
        inner.ReaderState = Basis1;

        ReadAccount(cache, TestItem.AddressA);

        Assert.That(inner.AccountReads, Is.EqualTo(1), "a written account with no budget must not displace a resident entry");
    }

    [TestCaseSource(nameof(CacheReadCases))]
    public void RetainedReader_RecordsCurrentCacheProbeButNotStaleBypass(CacheKind kind, bool found)
    {
        bool detailedMetricsEnabled = Db.Metrics.DetailedMetricsEnabled;
        FakePersistence inner = new()
        {
            AccountExists = found,
            SlotExists = found,
        };
        CarryForwardCachingPersistence cache = new(inner);
        try
        {
            cache.Clear();
            Db.Metrics.DetailedMetricsEnabled = true;
            long hitsBefore = GetHits(kind);
            long missesBefore = GetMisses(kind);

            using IPersistence.IPersistenceReader reader = cache.CreateReader();
            bool firstReadFound = Read(kind, reader, 1);
            bool secondReadFound = Read(kind, reader, 1);
            int innerReadsAfterCurrentReads = GetInnerReads(kind, inner);

            using (IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1)) { }
            inner.ReaderState = Basis1;
            bool staleReadFound = Read(kind, reader, 1);

            long hitsDelta = GetHits(kind) - hitsBefore;
            long missesDelta = GetMisses(kind) - missesBefore;
            int totalInnerReads = GetInnerReads(kind, inner);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(firstReadFound, Is.EqualTo(found), "the initial inner read result is preserved");
                Assert.That(secondReadFound, Is.EqualTo(found), "the cached result is preserved, including a missing value");
                Assert.That(staleReadFound, Is.EqualTo(found), "the stale reader delegates to the inner persistence");
                Assert.That(hitsDelta, Is.EqualTo(1), "the current reader's second read is a cache hit");
                Assert.That(missesDelta, Is.EqualTo(1), "only the current cache probe is a miss");
                Assert.That(innerReadsAfterCurrentReads, Is.EqualTo(1), "the second current read uses the cached result");
                Assert.That(totalInnerReads, Is.EqualTo(2), "the retained reader bypasses the cache after its generation becomes stale");
            }
        }
        finally
        {
            cache.Clear();
            Db.Metrics.DetailedMetricsEnabled = detailedMetricsEnabled;
        }
    }

    [Test]
    public void RetainedReader_AfterAccountRefresh_UsesItsInnerSnapshot()
    {
        Account oldAccount = Build.An.Account.WithNonce(1).TestObject;
        Account refreshedAccount = Build.An.Account.WithNonce(2).TestObject;
        FakePersistence inner = new() { AccountValue = oldAccount };
        CarryForwardCachingPersistence cache = new(inner);
        try
        {
            cache.Clear();
            ReadAccount(cache, Address);
            using IPersistence.IPersistenceReader reader = cache.CreateReader();

            using (IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1))
                batch.SetAccount(Address, refreshedAccount);
            inner.AccountValue = refreshedAccount;
            inner.ReaderState = Basis1;

            Account? read = reader.GetAccount(Address);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(read?.Nonce, Is.EqualTo(oldAccount.Nonce), "a retained reader must not serve a refreshed entry from a newer generation");
                Assert.That(inner.AccountReads, Is.EqualTo(2), "the retained reader reads through to its own inner snapshot");
            }
        }
        finally
        {
            cache.Clear();
        }
    }

    [TestCaseSource(nameof(CacheKinds))]
    public void Reader_CapturesDetailedMetricsEnabledAtConstruction(CacheKind kind)
    {
        bool detailedMetricsEnabled = Db.Metrics.DetailedMetricsEnabled;
        FakePersistence inner = new();
        CarryForwardCachingPersistence cache = new(inner);
        try
        {
            cache.Clear();
            long disabledReaderMissesBefore = GetMisses(kind);
            Db.Metrics.DetailedMetricsEnabled = false;
            using (IPersistence.IPersistenceReader reader = cache.CreateReader())
            {
                Db.Metrics.DetailedMetricsEnabled = true;
                Read(kind, reader, 1);
            }
            long disabledReaderMissesDelta = GetMisses(kind) - disabledReaderMissesBefore;

            Db.Metrics.DetailedMetricsEnabled = true;
            long enabledReaderMissesBefore = GetMisses(kind);
            using (IPersistence.IPersistenceReader reader = cache.CreateReader())
            {
                Db.Metrics.DetailedMetricsEnabled = false;
                Read(kind, reader, 2);
            }
            long enabledReaderMissesDelta = GetMisses(kind) - enabledReaderMissesBefore;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(disabledReaderMissesDelta, Is.Zero, "a false-to-true flag change does not affect an existing reader");
                Assert.That(enabledReaderMissesDelta, Is.EqualTo(1), "a true-to-false flag change does not affect an existing reader");
            }
        }
        finally
        {
            cache.Clear();
            Db.Metrics.DetailedMetricsEnabled = detailedMetricsEnabled;
        }
    }

    [TestCaseSource(nameof(CacheKinds))]
    public void OnCommitted_PublishesCacheCount(CacheKind kind)
    {
        FakePersistence inner = new();
        CarryForwardCachingPersistence cache = new(inner);
        try
        {
            cache.Clear();
            Read(kind, cache, 1);
            long countAfterFill = GetCount(kind);

            Invalidate(kind, cache, 1);
            long countAfterCommit = GetCount(kind);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(countAfterFill, Is.EqualTo(1));
                Assert.That(countAfterCommit, Is.EqualTo(kind == CacheKind.Account ? 1 : 0), "account writes refresh resident entries while slot writes invalidate them");
            }
        }
        finally
        {
            cache.Clear();
        }
    }

    [Test]
    public void Clear_PublishesZeroCacheCounts()
    {
        FakePersistence inner = new();
        CarryForwardCachingPersistence cache = new(inner);
        try
        {
            cache.Clear();
            ReadAccount(cache, Address);
            ReadSlot(cache, 1);
            long accountCountAfterFill = Metrics.CarryForwardAccountCount;
            long slotCountAfterFill = Metrics.CarryForwardSlotCount;

            cache.Clear();
            long accountCountAfterClear = Metrics.CarryForwardAccountCount;
            long slotCountAfterClear = Metrics.CarryForwardSlotCount;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(accountCountAfterFill, Is.EqualTo(1));
                Assert.That(slotCountAfterFill, Is.EqualTo(1));
                Assert.That(accountCountAfterClear, Is.Zero);
                Assert.That(slotCountAfterClear, Is.Zero);
            }
        }
        finally
        {
            cache.Clear();
        }
    }

    [TestCaseSource(nameof(CacheKinds))]
    public void CapacityWipe_PublishesPostRefillCount(CacheKind kind)
    {
        FakePersistence inner = new();
        CarryForwardCachingPersistence cache = new(inner, maxEntriesPerKind: 1);
        try
        {
            cache.Clear();
            long wipesBefore = Metrics.CarryForwardWipes;

            Read(kind, cache, 1);
            Read(kind, cache, 2);

            long wipesDelta = Metrics.CarryForwardWipes - wipesBefore;
            long countAfterRefill = GetCount(kind);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(wipesDelta, Is.EqualTo(1));
                Assert.That(countAfterRefill, Is.EqualTo(1), "the gauge is published after the overflowing fill");
            }
        }
        finally
        {
            cache.Clear();
        }
    }

    [Test]
    public void AccountRefresh_AtCapacityKeepsResidentUntilColdFillWipes()
    {
        FakePersistence inner = new();
        CarryForwardCachingPersistence cache = new(inner, maxEntriesPerKind: 1);
        try
        {
            cache.Clear();
            long wipesBefore = Metrics.CarryForwardWipes;
            ReadAccount(cache, Address);

            using (IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1))
                batch.SetAccount(Address, Build.An.Account.WithNonce(1).TestObject);
            inner.ReaderState = Basis1;
            long countAfterRefresh = Metrics.CarryForwardAccountCount;

            using (IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis1, Basis2))
                batch.SetAccount(TestItem.AddressB, Build.An.Account.WithNonce(2).TestObject);
            inner.ReaderState = Basis2;
            long countAfterFullAdmission = Metrics.CarryForwardAccountCount;

            ReadAccount(cache, TestItem.AddressB);
            long wipesDelta = Metrics.CarryForwardWipes - wipesBefore;
            long countAfterColdFill = Metrics.CarryForwardAccountCount;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(countAfterRefresh, Is.EqualTo(1), "refreshing a resident account retains it");
                Assert.That(countAfterFullAdmission, Is.EqualTo(1), "a committed account cannot displace a resident entry at capacity");
                Assert.That(wipesDelta, Is.EqualTo(1), "the later cold fill produces the documented wholesale wipe");
                Assert.That(countAfterColdFill, Is.EqualTo(1), "the account gauge is republished after the sawtooth refill");
            }
        }
        finally
        {
            cache.Clear();
        }
    }

    [Test]
    public void FailedSetAccount_AbandonsBatchWithoutAdvancingBasis()
    {
        Account persistedAccount = Build.An.Account.WithNonce(1).TestObject;
        Account rejectedAccount = Build.An.Account.WithNonce(2).TestObject;
        FakePersistence inner = new()
        {
            AccountValue = persistedAccount,
            ThrowOnSetAccount = true,
        };
        CarryForwardCachingPersistence cache = new(inner);
        try
        {
            cache.Clear();
            ReadAccount(cache, Address);
            IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1);
            try
            {
                Assert.Throws<InvalidOperationException>(() => batch.SetAccount(Address, rejectedAccount));
            }
            finally
            {
                Assert.DoesNotThrow(batch.Dispose);
            }

            inner.ReaderState = Basis1;
            Account? read = ReadAccountValue(cache, Address);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(read?.Nonce, Is.EqualTo(persistedAccount.Nonce));
                Assert.That(Metrics.CarryForwardAccountCount, Is.Zero, "the failed batch should leave no current cache entries");
                Assert.That(inner.AccountReads, Is.EqualTo(2), "the failed batch invalidated the cache instead of advancing its basis");
            }
        }
        finally
        {
            cache.Clear();
        }
    }

    [Test]
    public void FailedSetStorage_AbandonsBatchWithoutAdvancingBasis()
    {
        FakePersistence inner = new() { ThrowOnSetStorage = true };
        CarryForwardCachingPersistence cache = new(inner);
        try
        {
            cache.Clear();
            ReadSlot(cache, 1);
            IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1);
            SlotValue? rejectedValue = SlotValue.FromSpanWithoutLeadingZero([0x22]);
            try
            {
                Assert.Throws<InvalidOperationException>(() => batch.SetStorage(Address, 1, rejectedValue));
            }
            finally
            {
                Assert.DoesNotThrow(batch.Dispose);
            }

            inner.ReaderState = Basis1;
            ReadSlot(cache, 1);

            Assert.That(inner.SlotReads, Is.EqualTo(2), "the failed batch invalidated the cache instead of advancing its basis");
        }
        finally
        {
            cache.Clear();
        }
    }

    [Test]
    public void PartialBatchFailure_DoesNotPublishEarlierWrites()
    {
        Account persistedAccount = Build.An.Account.WithNonce(1).TestObject;
        Account writtenAccount = Build.An.Account.WithNonce(2).TestObject;
        FakePersistence inner = new()
        {
            AccountValue = persistedAccount,
            ThrowOnSetStorage = true,
        };
        CarryForwardCachingPersistence cache = new(inner);
        try
        {
            cache.Clear();
            ReadAccount(cache, Address);
            using (IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1))
            {
                batch.SetAccount(Address, writtenAccount);
                Assert.Throws<InvalidOperationException>(() => batch.SetStorage(Address, 1, null));
            }

            inner.AccountValue = writtenAccount;
            inner.ReaderState = Basis1;
            Account? read = ReadAccountValue(cache, Address);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(read?.Nonce, Is.EqualTo(writtenAccount.Nonce));
                Assert.That(inner.AccountReads, Is.EqualTo(2), "an earlier write from the abandoned batch was not published into the cache");
            }
        }
        finally
        {
            cache.Clear();
        }
    }

    [Test]
    public void DisposeFailure_AbandonsBatchWithoutAdvancingBasis()
    {
        Account persistedAccount = Build.An.Account.WithNonce(1).TestObject;
        Account writtenAccount = Build.An.Account.WithNonce(2).TestObject;
        FakePersistence inner = new()
        {
            AccountValue = persistedAccount,
            ThrowOnWriteBatchDispose = true,
        };
        CarryForwardCachingPersistence cache = new(inner);
        try
        {
            cache.Clear();
            ReadAccount(cache, Address);
            IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1);
            batch.SetAccount(Address, writtenAccount);
            Assert.Throws<InvalidOperationException>(batch.Dispose);

            inner.AccountValue = writtenAccount;
            inner.ReaderState = Basis1;
            Account? read = ReadAccountValue(cache, Address);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(read?.Nonce, Is.EqualTo(writtenAccount.Nonce));
                Assert.That(inner.AccountReads, Is.EqualTo(2), "a dispose failure did not invalidate the pending cache update");
            }
        }
        finally
        {
            cache.Clear();
        }
    }

    [Test]
    public void AbandonedBatch_ThenCommitToAnotherTarget_DoesNotServeTheUncommittedBranch()
    {
        Account persistedAccount = Build.An.Account.WithNonce(1).TestObject;
        Account abandonedAccount = Build.An.Account.WithNonce(2).TestObject;
        FakePersistence inner = new()
        {
            AccountValue = persistedAccount,
            ThrowOnSetStorage = true,
        };
        CarryForwardCachingPersistence cache = new(inner);
        try
        {
            cache.Clear();
            ReadAccount(cache, Address);

            using (IPersistence.IWriteBatch abandoned = cache.CreateWriteBatch(Basis0, Basis1))
            {
                abandoned.SetAccount(Address, abandonedAccount);
                Assert.Throws<InvalidOperationException>(() => abandoned.SetStorage(Address, 1, null));
            }

            // A reorg retargets the retry, so Basis1 is never committed.
            inner.ThrowOnSetStorage = false;
            using (IPersistence.IWriteBatch retry = cache.CreateWriteBatch(Basis0, Basis2))
                retry.SetAccount(TestItem.AddressB, persistedAccount);
            inner.ReaderState = Basis2;

            Account? read = ReadAccountValue(cache, Address);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(read?.Nonce, Is.EqualTo(persistedAccount.Nonce), "an account written by the abandoned branch must not be servable after a commit to another target");
                Assert.That(inner.AccountReads, Is.EqualTo(2), "the abandoned entry was dropped, so the read fell through to the database");
            }
        }
        finally
        {
            cache.Clear();
        }
    }

    private static IEnumerable<TestCaseData> SlotReadCases()
    {
        yield return new TestCaseData((Action<CarryForwardCachingPersistence, FakePersistence>)((_, _) => { }), 1)
        { TestName = "same_basis_served_from_cache" };

        yield return new TestCaseData((Action<CarryForwardCachingPersistence, FakePersistence>)((cache, inner) =>
        {
            using (IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1))
                batch.SetStorage(Address, 2, SlotValue.FromSpanWithoutLeadingZero([0x22]));
            inner.ReaderState = Basis1;
        }), 1)
        { TestName = "unwritten_slot_carried_forward" };

        yield return new TestCaseData((Action<CarryForwardCachingPersistence, FakePersistence>)((cache, inner) =>
        {
            using (IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1))
                batch.SetStorage(Address, 1, SlotValue.FromSpanWithoutLeadingZero([0x22]));
            inner.ReaderState = Basis1;
        }), 2)
        { TestName = "written_slot_invalidated" };

        yield return ClearingScenario("self_destruct_clears_cache", batch => batch.SelfDestruct(Address));
        yield return ClearingScenario("delete_account_range_clears_cache", batch => batch.DeleteAccountRange(default, default));
        yield return ClearingScenario("delete_storage_range_clears_cache", batch => batch.DeleteStorageRange(default, default, default));
        yield return ClearingScenario("set_account_raw_clears_cache", batch => batch.SetAccountRaw(default, new Account(1, 100)));
        yield return ClearingScenario("set_storage_raw_encoded_clears_cache", batch => batch.SetStorageRawEncoded(default, default, default));

        yield return new TestCaseData((Action<CarryForwardCachingPersistence, FakePersistence>)((cache, _) =>
        {
            // Advance the cache basis but leave the reader behind, so it must bypass the cache.
            using (cache.CreateWriteBatch(Basis0, Basis1)) { }
        }), 2)
        { TestName = "reader_behind_basis_bypasses" };
    }

    private static IEnumerable<TestCaseData> CacheKinds()
    {
        yield return new TestCaseData(CacheKind.Account) { TestName = "account" };
        yield return new TestCaseData(CacheKind.Slot) { TestName = "slot" };
    }

    private static IEnumerable<TestCaseData> CacheReadCases()
    {
        yield return new TestCaseData(CacheKind.Account, true) { TestName = "account_found" };
        yield return new TestCaseData(CacheKind.Account, false) { TestName = "account_not_found" };
        yield return new TestCaseData(CacheKind.Slot, true) { TestName = "slot_found" };
        yield return new TestCaseData(CacheKind.Slot, false) { TestName = "slot_not_found" };
    }

    private static TestCaseData ClearingScenario(string name, Action<IPersistence.IWriteBatch> write) =>
        new((Action<CarryForwardCachingPersistence, FakePersistence>)((cache, inner) =>
        {
            using (IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1))
                write(batch);
            inner.ReaderState = Basis1;
        }), 2)
        { TestName = name };

    private static void ReadSlot(IPersistence persistence, UInt256 slot)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        SlotValue value = default;
        reader.TryGetSlot(Address, slot, ref value);
    }

    private static void ReadAccount(IPersistence persistence, Address address) => ReadAccountValue(persistence, address);

    private static Account? ReadAccountValue(IPersistence persistence, Address address)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        return reader.GetAccount(address);
    }

    private static bool Read(CacheKind kind, IPersistence persistence, int key)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        return Read(kind, reader, key);
    }

    private static bool Read(CacheKind kind, IPersistence.IPersistenceReader reader, int key)
    {
        if (kind == CacheKind.Account)
        {
            return reader.GetAccount(GetAddress(key)) is not null;
        }

        UInt256 slot = new((ulong)key);
        SlotValue value = default;
        return reader.TryGetSlot(Address, slot, ref value);
    }

    private static void Invalidate(CacheKind kind, CarryForwardCachingPersistence cache, int key)
    {
        using IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1);
        if (kind == CacheKind.Account)
        {
            batch.SetAccount(GetAddress(key), new Account(1, 100));
            return;
        }

        UInt256 slot = new((ulong)key);
        SlotValue value = SlotValue.FromSpanWithoutLeadingZero([0x22]);
        batch.SetStorage(Address, slot, value);
    }

    private static Address GetAddress(int key) => key == 1 ? Address : TestItem.AddressB;

    private static long GetHits(CacheKind kind) => kind == CacheKind.Account
        ? Metrics.CarryForwardAccountHits
        : Metrics.CarryForwardSlotHits;

    private static long GetMisses(CacheKind kind) => kind == CacheKind.Account
        ? Metrics.CarryForwardAccountMisses
        : Metrics.CarryForwardSlotMisses;

    private static long GetCount(CacheKind kind) => kind == CacheKind.Account
        ? Metrics.CarryForwardAccountCount
        : Metrics.CarryForwardSlotCount;

    private static int GetInnerReads(CacheKind kind, FakePersistence inner) => kind == CacheKind.Account
        ? inner.AccountReads
        : inner.SlotReads;

    public sealed class FakePersistence : IPersistence
    {
        public StateId ReaderState = Basis0;
        public int AccountReads;
        public int SlotReads;
        public bool AccountExists = true;
        public bool SlotExists = true;
        public Account? AccountValue = new(1, 100);
        public SlotValue SlotValueValue = SlotValue.FromSpanWithoutLeadingZero([0x11]);
        public bool ThrowOnSetAccount;
        public bool ThrowOnSetStorage;
        public bool ThrowOnWriteBatchDispose;

        public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None) => new Reader(this);
        public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None) => new WriteBatch(this);
        public void Flush() { }
        public void Clear() { }

        private sealed class Reader(FakePersistence parent) : IPersistence.IPersistenceReader
        {
            private readonly StateId _state = parent.ReaderState;
            private readonly bool _accountExists = parent.AccountExists;
            private readonly bool _slotExists = parent.SlotExists;
            private readonly Account? _accountValue = parent.AccountValue;
            private readonly SlotValue _slotValue = parent.SlotValueValue;

            public Account? GetAccount(Address address)
            {
                parent.AccountReads++;
                return _accountExists ? _accountValue : null;
            }

            public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue)
            {
                parent.SlotReads++;
                if (!_slotExists) return false;
                outValue = _slotValue;
                return true;
            }

            public StateId CurrentState => _state;
            public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => null;
            public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => null;
            public byte[]? GetAccountRaw(in ValueHash256 addrHash) => null;
            public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref SlotValue value) => false;
            public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => throw new NotSupportedException();
            public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => throw new NotSupportedException();
            public bool IsPreimageMode => false;
            public void Dispose() { }
        }

        private sealed class WriteBatch(FakePersistence parent) : IPersistence.IWriteBatch
        {
            public void SelfDestruct(Address addr) { }

            public void SetAccount(Address addr, Account? account)
            {
                if (parent.ThrowOnSetAccount) throw new InvalidOperationException();
            }

            public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value)
            {
                if (parent.ThrowOnSetStorage) throw new InvalidOperationException();
            }

            public void SetStateTrieNode(in TreePath path, scoped ReadOnlySpan<byte> rlp) { }
            public void SetStorageTrieNode(Hash256 address, in TreePath path, scoped ReadOnlySpan<byte> rlp) { }
            public void SetStorageRawEncoded(in ValueHash256 addrHash, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue) { }
            public void SetAccountRaw(in ValueHash256 addrHash, Account account) { }
            public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) { }
            public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath) { }
            public void DeleteStateTrieNodeRange(in ValueHash256 from, in ValueHash256 to) { }
            public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in ValueHash256 from, in ValueHash256 to) { }
            public void Dispose()
            {
                if (parent.ThrowOnWriteBatchDispose) throw new InvalidOperationException();
            }
        }
    }
}
