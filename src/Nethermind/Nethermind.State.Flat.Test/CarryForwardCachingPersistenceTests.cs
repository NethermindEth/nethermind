// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Init.Modules;
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

    [TestCase(false, 1, true, TestName = "WriteBatch_SlotSetWithinCapacity_KeptForTheNextBatch")]
    [TestCase(false, 2, false, TestName = "WriteBatch_SlotSetBeyondCapacity_NotKept")]
    [TestCase(true, 1, true, TestName = "WriteBatch_AccountSetWithinCapacity_KeptForTheNextBatch")]
    [TestCase(true, 2, false, TestName = "WriteBatch_AccountSetBeyondCapacity_NotKept")]
    public void WriteBatch_CommittedWrittenSet_KeptOnlyWithinTheCacheCapacity(bool accounts, int written, bool kept)
    {
        FakePersistence inner = new();
        CarryForwardCachingPersistence cache = new(inner, maxEntriesPerKind: 1);

        using (IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1))
        {
            for (int i = 0; i < written; i++)
            {
                if (accounts) batch.SetAccount(TestItem.Addresses[i], TestItem.GenerateRandomAccount());
                else batch.SetStorage(Address, (UInt256)i, BaseFlatPersistence.DecodeSlotValue([0x22]));
            }
        }

        Assert.That(accounts ? cache.HasSpareWrittenAccounts : cache.HasSpareWrittenSlots, Is.EqualTo(kept));
    }

    [TestCaseSource(nameof(CacheReadCases))]
    public async Task RetainedReader_RecordsCurrentCacheProbeButNotStaleBypass(CacheKind kind, bool found)
    {
        bool detailedMetricsEnabled = Db.Metrics.DetailedMetricsEnabled;
        FakePersistence inner = new()
        {
            AccountExists = found,
            SlotExists = found,
        };
        await using IContainer container = CreateCacheContainer();
        CarryForwardCachingPersistence cache = ResolveCache(container, inner);
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
    public async Task Reader_CapturesDetailedMetricsEnabledAtConstruction(CacheKind kind)
    {
        bool detailedMetricsEnabled = Db.Metrics.DetailedMetricsEnabled;
        FakePersistence inner = new();
        await using IContainer container = CreateCacheContainer();
        CarryForwardCachingPersistence cache = ResolveCache(container, inner);
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
    public async Task OnCommitted_IncrementalInvalidationPublishesCacheCount(CacheKind kind)
    {
        FakePersistence inner = new();
        await using IContainer container = CreateCacheContainer();
        CarryForwardCachingPersistence cache = ResolveCache(container, inner);
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
    public async Task Clear_PublishesZeroCacheCounts()
    {
        FakePersistence inner = new();
        await using IContainer container = CreateCacheContainer();
        CarryForwardCachingPersistence cache = ResolveCache(container, inner);
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
    public async Task CapacityWipe_PublishesPostRefillCount(CacheKind kind)
    {
        FakePersistence inner = new();
        await using IContainer container = CreateCacheContainer();
        CarryForwardCachingPersistence cache = ResolveCache(container, inner, maxEntriesPerKind: 1);
        try
        {
            cache.Clear();
            long wipesBefore = GetWipes(kind);
            long otherWipesBefore = GetWipes(GetOtherKind(kind));

            Read(kind, cache, 1);
            Read(kind, cache, 2);

            long wipesDelta = GetWipes(kind) - wipesBefore;
            long otherWipesDelta = GetWipes(GetOtherKind(kind)) - otherWipesBefore;
            long countAfterRefill = GetCount(kind);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(wipesDelta, Is.EqualTo(1));
                Assert.That(otherWipesDelta, Is.Zero);
                Assert.That(countAfterRefill, Is.EqualTo(1), "the gauge is published after the overflowing fill");
            }
        }
        finally
        {
            cache.Clear();
        }
    }

    [Test]
    public async Task CreateReader_SyncReader_BypassesTheCache()
    {
        FakePersistence inner = new();
        await using IContainer container = CreateCacheContainer();
        CarryForwardCachingPersistence cache = ResolveCache(container, inner);

        for (int i = 0; i < 2; i++)
        {
            using IPersistence.IPersistenceReader reader = cache.CreateReader(ReaderFlags.Sync);
            UInt256 value = default;
            reader.TryGetSlot(Address, 1, ref value);
        }

        Assert.That(inner.SlotReads, Is.EqualTo(2));
    }

    [Test]
    public async Task RandomOperations_EveryReadMatchesThePersistenceAtTheReaderState([Values(4, 8, 262144)] int capacity)
    {
        const int operations = 1_000_000;
        const int maxOpenReaders = 8;
        bool detailedMetricsEnabled = Db.Metrics.DetailedMetricsEnabled;
        Db.Metrics.DetailedMetricsEnabled = true;
        long hits;
        int reads = 0;
        List<string> mismatches = [];
        try
        {
            long hitsBefore = Metrics.CarryForwardSlotHits;
            Random random = new(capacity);
            ModelPersistence model = new(addresses: 4, slotsPerAddress: 8);
            await using IContainer container = CreateCacheContainer();
            CarryForwardCachingPersistence cache = ResolveCache(container, model, capacity);
            List<(IPersistence.IPersistenceReader Reader, ModelPersistence.State State)> readers = [];

            for (int i = 0; i < operations && mismatches.Count < 10; i++)
            {
                int operation = random.Next(1000);
                if (operation < 850)
                {
                    if (readers.Count == 0 || random.Next(4) == 0)
                    {
                        if (readers.Count == maxOpenReaders)
                        {
                            readers[0].Reader.Dispose();
                            readers.RemoveAt(0);
                        }
                        readers.Add((cache.CreateReader(), ModelPersistence.LastReaderState!));
                    }

                    (IPersistence.IPersistenceReader reader, ModelPersistence.State state) = random.Next(2) == 0
                        ? readers[^1]
                        : readers[random.Next(readers.Count)];
                    reads++;
                    string? mismatch = random.Next(4) == 0
                        ? model.CheckAccountRead(reader, state, random)
                        : model.CheckSlotRead(reader, state, random);
                    if (mismatch is not null) mismatches.Add($"operation {i}: {mismatch}");
                }
                else if (operation < 880)
                {
                    if (readers.Count == 0) continue;
                    int index = random.Next(readers.Count);
                    readers[index].Reader.Dispose();
                    readers.RemoveAt(index);
                }
                else if (operation < 998)
                {
                    model.CommitRandomBatch(cache, random, clearAllShare: 0.03);
                }
                else
                {
                    // Snap sync clears the persistence before anything reads it, so no reader spans the clear.
                    foreach ((IPersistence.IPersistenceReader reader, _) in readers) reader.Dispose();
                    readers.Clear();
                    cache.Clear();
                }
            }

            foreach ((IPersistence.IPersistenceReader reader, _) in readers) reader.Dispose();
            hits = Metrics.CarryForwardSlotHits - hitsBefore;
        }
        finally
        {
            Db.Metrics.DetailedMetricsEnabled = detailedMetricsEnabled;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mismatches, Is.Empty);
            Assert.That(reads, Is.GreaterThan(operations / 2));
            Assert.That(hits, Is.GreaterThan(reads / 100), "the cache served a share of the reads");
        }
    }

    [TestCase(3, 64)]
    [TestCase(3, 262144)]
    [TestCase(30, 64, Explicit = true, Reason = "Long stress run")]
    [TestCase(30, 262144, Explicit = true, Reason = "Long stress run")]
    public async Task ConcurrentReadersAndCommitter_ReadEachReaderState(int seconds, int capacity)
    {
        const int readerThreads = 16;
        const int readsPerReader = 64;
        bool detailedMetricsEnabled = Db.Metrics.DetailedMetricsEnabled;
        Db.Metrics.DetailedMetricsEnabled = true;
        long hits;
        long reads = 0;
        long commits = 0;
        MismatchLog mismatches = new();
        try
        {
            long hitsBefore = Metrics.CarryForwardSlotHits;
            ModelPersistence model = new(addresses: 8, slotsPerAddress: 16);
            await using IContainer container = CreateCacheContainer();
            CarryForwardCachingPersistence cache = ResolveCache(container, model, capacity);
            using CancellationTokenSource stop = new(TimeSpan.FromSeconds(seconds));

            Task committer = Task.Factory.StartNew(() =>
            {
                Random random = new(1);
                while (!stop.IsCancellationRequested)
                {
                    model.CommitRandomBatch(cache, random, clearAllShare: 0.005);
                    Interlocked.Increment(ref commits);
                    Thread.SpinWait(random.Next(2000));
                }
            }, TaskCreationOptions.LongRunning);

            Task[] readerTasks = new Task[readerThreads];
            for (int t = 0; t < readerThreads; t++)
            {
                int seed = t + 2;
                readerTasks[t] = Task.Factory.StartNew(() =>
                {
                    Random random = new(seed);
                    while (!stop.IsCancellationRequested)
                    {
                        using IPersistence.IPersistenceReader reader = cache.CreateReader();
                        ModelPersistence.State state = ModelPersistence.LastReaderState!;
                        for (int i = 0; i < readsPerReader; i++)
                        {
                            string? mismatch = random.Next(8) == 0
                                ? model.CheckAccountRead(reader, state, random)
                                : model.CheckSlotRead(reader, state, random);
                            if (mismatch is not null) mismatches.Add(mismatch);
                        }
                        Interlocked.Add(ref reads, readsPerReader);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            Task.WaitAll([committer, .. readerTasks]);
            hits = Metrics.CarryForwardSlotHits - hitsBefore;
        }
        finally
        {
            Db.Metrics.DetailedMetricsEnabled = detailedMetricsEnabled;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mismatches.Count, Is.Zero, mismatches.ToString());
            Assert.That(Interlocked.Read(ref commits), Is.GreaterThan(100));
            Assert.That(Interlocked.Read(ref reads), Is.GreaterThan(100_000));
            Assert.That(hits, Is.GreaterThan(1000), "readers were served by the cache while it was being written");
        }
    }

    private const int PatternVersionBits = 40;

    /// <summary>A slot value, unique per <paramref name="key"/> and <paramref name="version"/>, that fills all four limbs.</summary>
    private static UInt256 Pattern(int key, ulong version)
    {
        ulong u0 = ((ulong)key << PatternVersionBits) | version;
        ulong u1 = u0 * 0x9E3779B97F4A7C15UL;
        ulong u2 = u1 ^ 0xBF58476D1CE4E5B9UL;
        return new UInt256(u0, u1, u2, ~u0);
    }

    private static IEnumerable<TestCaseData> SlotReadCases()
    {
        yield return new TestCaseData((Action<CarryForwardCachingPersistence, FakePersistence>)((_, _) => { }), 1)
        { TestName = "same_basis_served_from_cache" };

        yield return new TestCaseData((Action<CarryForwardCachingPersistence, FakePersistence>)((cache, inner) =>
        {
            using (IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1))
                batch.SetStorage(Address, 2, BaseFlatPersistence.DecodeSlotValue([0x22]));
            inner.ReaderState = Basis1;
        }), 1)
        { TestName = "unwritten_slot_carried_forward" };

        yield return new TestCaseData((Action<CarryForwardCachingPersistence, FakePersistence>)((cache, inner) =>
        {
            using (IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1))
                batch.SetStorage(Address, 1, BaseFlatPersistence.DecodeSlotValue([0x22]));
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

    private static IContainer CreateCacheContainer() => new ContainerBuilder()
        .AddModule(new FlatWorldStateModule(new FlatDbConfig()))
        .Build();

    private static CarryForwardCachingPersistence ResolveCache(IContainer container, IPersistence inner, int? maxEntriesPerKind = null)
    {
        if (maxEntriesPerKind is int capacity)
        {
            return container.Resolve<CarryForwardCachingPersistence>(
                TypedParameter.From<IPersistence>(inner),
                new NamedParameter("maxEntriesPerKind", capacity));
        }

        return container.Resolve<CarryForwardCachingPersistence>(TypedParameter.From<IPersistence>(inner));
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
        UInt256 value = default;
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
        UInt256 value = default;
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
        batch.SetStorage(Address, slot, BaseFlatPersistence.DecodeSlotValue([0x22]));
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

    private static long GetWipes(CacheKind kind) => kind == CacheKind.Account
        ? Metrics.CarryForwardAccountWipes
        : Metrics.CarryForwardSlotWipes;

    private static CacheKind GetOtherKind(CacheKind kind) => kind == CacheKind.Account
        ? CacheKind.Slot
        : CacheKind.Account;

    private static int GetInnerReads(CacheKind kind, FakePersistence inner) => kind == CacheKind.Account
        ? inner.AccountReads
        : inner.SlotReads;

    /// <summary>Counts mismatches from many threads and keeps the first few messages.</summary>
    private sealed class MismatchLog
    {
        private const int Kept = 5;
        private readonly string?[] _messages = new string?[Kept];
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Add(string mismatch)
        {
            int index = Interlocked.Increment(ref _count) - 1;
            if (index < Kept) Volatile.Write(ref _messages[index], mismatch);
        }

        public override string ToString() => string.Join(Environment.NewLine, _messages);
    }

    /// <summary>
    /// A versioned in-memory persistence: each commit publishes an immutable <see cref="State"/>, and a reader keeps the
    /// state it was created at, like a database snapshot. It is the reference the cache's reads are checked against.
    /// </summary>
    private sealed class ModelPersistence : IPersistence
    {
        [ThreadStatic] private static State? _lastReaderState;

        private readonly Address[] _addresses;
        private readonly UInt256[] _slots;
        private readonly Dictionary<ValueHash256, Address> _addressesByHash = [];
        private readonly Dictionary<ValueHash256, UInt256> _slotsByHash = [];
        private State _state = new(0, ImmutableDictionary<Address, Account>.Empty, ImmutableDictionary<(Address, UInt256), UInt256>.Empty);

        public ModelPersistence(int addresses, int slotsPerAddress)
        {
            _addresses = new Address[addresses];
            for (int i = 0; i < addresses; i++)
            {
                _addresses[i] = TestItem.Addresses[i];
                _addressesByHash[HashOf(_addresses[i])] = _addresses[i];
            }

            _slots = new UInt256[slotsPerAddress];
            for (int i = 0; i < slotsPerAddress; i++)
            {
                // Small slots and hashed-looking ones, as contracts use both.
                _slots[i] = i % 2 == 0 ? new UInt256((ulong)i) : new UInt256(Keccak.Compute([(byte)i]).Bytes, isBigEndian: true);
                _slotsByHash[HashOf(_slots[i])] = _slots[i];
            }
        }

        /// <summary>The state the calling thread's last <see cref="CreateReader"/> call pinned.</summary>
        public static State? LastReaderState => _lastReaderState;

        public State Current => Volatile.Read(ref _state);

        public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None)
        {
            State state = Current;
            _lastReaderState = state;
            return new Reader(state);
        }

        public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None) =>
            new WriteBatch(this, Current, to.BlockNumber);

        public void Flush() { }

        public void Clear()
        {
            State current = Current;
            Volatile.Write(ref _state, current with { Accounts = current.Accounts.Clear(), Slots = current.Slots.Clear() });
        }

        /// <summary>Commits one random write batch through <paramref name="cache"/>.</summary>
        /// <param name="clearAllShare">The share of batches carrying a write that makes the cache clear everything.</param>
        public void CommitRandomBatch(CarryForwardCachingPersistence cache, Random random, double clearAllShare)
        {
            State current = Current;
            ulong block = current.Block + 1;
            using IPersistence.IWriteBatch batch = cache.CreateWriteBatch(current.Id, new StateId(block, Keccak.EmptyTreeHash));

            int writes = random.Next(6);
            for (int i = 0; i < writes; i++)
            {
                int key = random.Next(_addresses.Length * _slots.Length);
                Address address = _addresses[key / _slots.Length];
                UInt256 slot = _slots[key % _slots.Length];
                if (random.Next(3) == 0)
                {
                    batch.SetAccount(address, random.Next(5) == 0 ? null : new Account(block, (UInt256)key));
                }
                else
                {
                    batch.SetStorage(address, slot, random.Next(5) == 0 ? null : Pattern(key, block));
                }
            }

            if (random.NextDouble() >= clearAllShare) return;

            int keyToClear = random.Next(_addresses.Length * _slots.Length);
            Address clearedAddress = _addresses[keyToClear / _slots.Length];
            UInt256 clearedSlot = _slots[keyToClear % _slots.Length];
            switch (random.Next(5))
            {
                case 0:
                    batch.SelfDestruct(clearedAddress);
                    break;
                case 1:
                    batch.SetStorageRawEncoded(HashOf(clearedAddress), HashOf(clearedSlot),
                        Pattern(keyToClear, block).ToBigEndian());
                    break;
                case 2:
                    batch.SetAccountRaw(HashOf(clearedAddress), new Account(block + 1, (UInt256)keyToClear));
                    break;
                case 3:
                    ValueHash256 slotHash = HashOf(clearedSlot);
                    batch.DeleteStorageRange(HashOf(clearedAddress), slotHash, random.Next(2) == 0 ? slotHash : ValueKeccak.Zero);
                    break;
                default:
                    ValueHash256 addressHash = HashOf(clearedAddress);
                    batch.DeleteAccountRange(addressHash, addressHash);
                    break;
            }
        }

        public string? CheckSlotRead(IPersistence.IPersistenceReader reader, State state, Random random)
        {
            Address address = _addresses[random.Next(_addresses.Length)];
            UInt256 slot = _slots[random.Next(_slots.Length)];
            UInt256 value = default;
            bool found = reader.TryGetSlot(address, slot, ref value);
            bool expectedFound = state.Slots.TryGetValue((address, slot), out UInt256 expected);
            return found == expectedFound && (!found || value == expected)
                ? null
                : $"slot {address}:{slot} at block {state.Block}: read ({found}, {value}), expected ({expectedFound}, {expected})";
        }

        public string? CheckAccountRead(IPersistence.IPersistenceReader reader, State state, Random random)
        {
            Address address = _addresses[random.Next(_addresses.Length)];
            Account? account = reader.GetAccount(address);
            Account? expected = state.Accounts.GetValueOrDefault(address);
            return Equals(account, expected)
                ? null
                : $"account {address} at block {state.Block}: read {account}, expected {expected}";
        }

        private static ValueHash256 HashOf(Address address) => ValueKeccak.Compute(address.Bytes);

        private static ValueHash256 HashOf(in UInt256 slot) => ValueKeccak.Compute(slot.ToBigEndian());

        public sealed record State(ulong Block, ImmutableDictionary<Address, Account> Accounts, ImmutableDictionary<(Address, UInt256), UInt256> Slots)
        {
            public StateId Id => new(Block, Keccak.EmptyTreeHash);
        }

        private sealed class Reader(State state) : IPersistence.IPersistenceReader
        {
            public Account? GetAccount(Address address) => state.Accounts.GetValueOrDefault(address);

            public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue)
            {
                if (!state.Slots.TryGetValue((address, slot), out UInt256 value)) return false;
                outValue = value;
                return true;
            }

            public StateId CurrentState => state.Id;
            public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => null;
            public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => null;
            public byte[]? GetAccountRaw(in ValueHash256 addrHash) => null;
            public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => false;
            public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => throw new NotSupportedException();
            public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => throw new NotSupportedException();
            public bool IsPreimageMode => false;
            public void Dispose() { }
        }

        /// <summary>Applies its writes in order and publishes them as one new state on dispose, as a database batch does.</summary>
        private sealed class WriteBatch(ModelPersistence model, State from, ulong block) : IPersistence.IWriteBatch
        {
            private readonly ImmutableDictionary<Address, Account>.Builder _accounts = from.Accounts.ToBuilder();
            private readonly ImmutableDictionary<(Address, UInt256), UInt256>.Builder _slots = from.Slots.ToBuilder();

            public void SelfDestruct(Address addr) => RemoveSlots(addr, ValueKeccak.Zero, ValueKeccak.Zero, all: true);

            public void SetAccount(Address addr, Account? account)
            {
                if (account is null) _accounts.Remove(addr);
                else _accounts[addr] = account;
            }

            public void SetStorage(Address addr, in UInt256 slot, in UInt256? value)
            {
                if (value is null) _slots.Remove((addr, slot));
                else _slots[(addr, slot)] = value.Value;
            }

            public void SetStorageRawEncoded(in ValueHash256 addrHash, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue) =>
                _slots[(model._addressesByHash[addrHash], model._slotsByHash[slotHash])] = new UInt256(rlpValue, isBigEndian: true);

            public void SetAccountRaw(in ValueHash256 addrHash, Account account) => _accounts[model._addressesByHash[addrHash]] = account;

            public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath)
            {
                foreach (Address address in model._addresses)
                {
                    ValueHash256 hash = HashOf(address);
                    if (hash.CompareTo(fromPath) >= 0 && hash.CompareTo(toPath) <= 0) _accounts.Remove(address);
                }
            }

            // A zero upper bound stands for the whole range here.
            public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath) =>
                RemoveSlots(model._addressesByHash[addressHash], fromPath, toPath, all: toPath == ValueKeccak.Zero);

            public void SetStateTrieNode(in TreePath path, scoped ReadOnlySpan<byte> rlp) { }
            public void SetStorageTrieNode(Hash256 address, in TreePath path, scoped ReadOnlySpan<byte> rlp) { }
            public void DeleteStateTrieNodeRange(in ValueHash256 from, in ValueHash256 to) { }
            public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in ValueHash256 from, in ValueHash256 to) { }

            public void Dispose() => Volatile.Write(ref model._state, new State(block, _accounts.ToImmutable(), _slots.ToImmutable()));

            private void RemoveSlots(Address address, in ValueHash256 fromPath, in ValueHash256 toPath, bool all)
            {
                foreach (UInt256 slot in model._slots)
                {
                    ValueHash256 hash = HashOf(slot);
                    if (all || (hash.CompareTo(fromPath) >= 0 && hash.CompareTo(toPath) <= 0)) _slots.Remove((address, slot));
                }
            }
        }
    }

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

            public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue)
            {
                parent.SlotReads++;
                if (!parent.SlotExists) return false;
                outValue = BaseFlatPersistence.DecodeSlotValue([0x11]);
                return true;
            }

            public StateId CurrentState => parent.ReaderState;
            public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => null;
            public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => null;
            public byte[]? GetAccountRaw(in ValueHash256 addrHash) => null;
            public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => false;
            public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => throw new NotSupportedException();
            public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => throw new NotSupportedException();
            public bool IsPreimageMode => false;
            public void Dispose() { }
        }
    }
}
