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
    public void OnCommitted_IncrementalInvalidationPublishesCacheCount(CacheKind kind)
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
                Assert.That(countAfterCommit, Is.Zero, "this branch evicts a written account or slot at commit");
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

    private static void ReadAccount(IPersistence persistence, Address address)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        reader.GetAccount(address);
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

        public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None) => new Reader(this);
        public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None) => new FakeWriteBatch();
        public void Flush() { }
        public void Clear() { }

        private sealed class Reader(FakePersistence parent) : IPersistence.IPersistenceReader
        {
            public Account? GetAccount(Address address)
            {
                parent.AccountReads++;
                return parent.AccountExists ? new Account(1, 100) : null;
            }

            public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue)
            {
                parent.SlotReads++;
                if (!parent.SlotExists) return false;
                outValue = SlotValue.FromSpanWithoutLeadingZero([0x11]);
                return true;
            }

            public StateId CurrentState => parent.ReaderState;
            public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => null;
            public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => null;
            public byte[]? GetAccountRaw(in ValueHash256 addrHash) => null;
            public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref SlotValue value) => false;
            public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => throw new NotSupportedException();
            public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => throw new NotSupportedException();
            public bool IsPreimageMode => false;
            public void Dispose() { }
        }
    }
}
