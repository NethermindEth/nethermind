// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

[TestFixture]
public class BaseFlatPersistenceReaderTests
{
    // Regression: a slot value longer than BaseFlatPersistence.StorageValueSize must fail loudly instead of underflowing
    // the unchecked Unsafe.InitBlockUnaligned in TryGetStorage (which produced a wild memset / SIGSEGV).
    // Shorter values are right-aligned into the 32-byte slot with leading zeros.
    // Cases use rlpWrapSlots:false (the corrupted-DB path). There is no rlpWrapSlots:true throwing case: the
    // value is read into a RlpSlotValueBufferSize (33) byte buffer, so the decoded length cannot exceed 32 —
    // see TryGetStorage_RlpWrapped_DecodesToSlotValue for the wrapped golden path.
    [TestCase(33, true)]
    [TestCase(32, false)]
    [TestCase(16, false)]
    [TestCase(1, false)]
    public void TryGetStorage_RejectsOverLengthValue_ElseRightAligns(int valueLength, bool shouldThrow)
    {
        byte[] value = new byte[valueLength];
        for (int i = 0; i < valueLength; i++) value[i] = (byte)(i + 1);

        FixedValueStore store = new(value);
        BaseFlatPersistence.Reader reader = new(store, store, isPreimageMode: false, rlpWrapSlots: false);

        if (shouldThrow)
        {
            Assert.Throws<InvalidConfigurationException>(() =>
            {
                UInt256 outValue = default;
                reader.TryGetStorage(default, default, ref outValue);
            });
            return;
        }

        UInt256 result = default;
        bool found = reader.TryGetStorage(default, default, ref result);

        byte[] expected = new byte[BaseFlatPersistence.StorageValueSize];
        value.CopyTo(expected, BaseFlatPersistence.StorageValueSize - valueLength);

        Assert.That(found, Is.True);
        Assert.That(result.ToBigEndian(), Is.EqualTo(expected));
    }

    // Golden path: a correctly RLP-wrapped 32-byte value (0xa0 + 32 = 33 bytes on disk) decodes cleanly with
    // rlpWrapSlots:true and must not trip the over-length guard (which checks the decoded length, not 33).
    [Test]
    public void TryGetStorage_RlpWrapped_DecodesToSlotValue()
    {
        byte[] payload = new byte[BaseFlatPersistence.StorageValueSize];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i + 1);
        byte[] rlp = Rlp.Encode(payload).Bytes; // 0xa0 + 32 bytes

        FixedValueStore store = new(rlp);
        BaseFlatPersistence.Reader reader = new(store, store, isPreimageMode: false, rlpWrapSlots: true);

        UInt256 result = default;
        bool found = reader.TryGetStorage(default, default, ref result);

        Assert.That(found, Is.True);
        Assert.That(result.ToBigEndian(), Is.EqualTo(payload));
    }

    // A prefetch must warm exactly the keys the scalar reads ask for, in both storage key layouts and across the
    // 64-key batch boundary; any other key leaves the reads cold.
    [Test]
    public void Prefetch_SendsTheKeysTheScalarReadsUse([Values] bool fullAddressStorageKey)
    {
        const int count = 70;
        RecordingStore accountStore = new();
        RecordingStore storageStore = new();
        BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader> reader = new(
            new BaseFlatPersistence.Reader(accountStore, storageStore, fullAddressStorageKey: fullAddressStorageKey));
        Address[] accounts = new Address[count];
        StorageCell[] slots = new StorageCell[count];
        for (int i = 0; i < count; i++)
        {
            accounts[i] = Address.FromNumber((UInt256)(i + 1));
            slots[i] = new StorageCell(accounts[i % 3], (UInt256)(i * 7919));
        }

        PlanAndPrefetch(reader, accounts, slots, CancellationToken.None);
        // The timed read of the first missed sample is not a scalar read of the block.
        accountStore.ReadKeys.Clear();
        storageStore.ReadKeys.Clear();
        UInt256 value = default;
        foreach (Address address in accounts) reader.GetAccount(address);
        foreach (StorageCell slot in slots) reader.TryGetSlot(slot.Address, slot.Index, ref value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountStore.PrefetchBatches, Is.EqualTo(new[] { 64, 6 }));
            Assert.That(storageStore.PrefetchBatches, Is.EqualTo(new[] { 64, 6 }));
            Assert.That(accountStore.PrefetchedKeys, Is.EqualTo(accountStore.ReadKeys));
            Assert.That(storageStore.PrefetchedKeys, Is.EqualTo(storageStore.ReadKeys));
        }
    }

    // Spread-out probes decide for the whole block: all of them cached skips the prefetch, one cold sample sends it.
    // 70 keys are probed at 0, 8, 17, 26, 35, 43, 52 and 61; a single cold key between probes is not seen and is
    // read as on master.
    [TestCase(-1, false)]
    [TestCase(0, true)]
    [TestCase(35, true)]
    [TestCase(61, true)]
    [TestCase(40, false)]
    [TestCase(69, false)]
    public void Prefetch_IsSentOnlyWhenASampledKeyIsCold(int coldIndex, bool expectPrefetch)
    {
        const int count = 70;
        Address[] accounts = Accounts(count);
        Span<byte> key = stackalloc byte[BaseFlatPersistence.AccountKeyLength];
        string? coldKey = coldIndex < 0 ? null : Convert.ToHexString(BaseFlatPersistence.EncodeAccountKeyHashed(key, accounts[coldIndex].ToAccountPath));
        RecordingStore accountStore = new(cached: k => k != coldKey);
        BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader> reader = new(new BaseFlatPersistence.Reader(accountStore, new RecordingStore()));
        long skipped = Nethermind.Db.Metrics.PrefetchesSkippedCached;

        PlanAndPrefetch(reader, accounts, [], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountStore.PrefetchedKeys.Count, Is.EqualTo(expectPrefetch ? count : 0));
            Assert.That(Nethermind.Db.Metrics.PrefetchesSkippedCached - skipped, Is.EqualTo(expectPrefetch ? 0 : 1));
        }
    }

    // A sampled key missing from the block cache but read at memory speed is in the OS page cache, where a prefetch
    // only repeats the read: no sample is slow, so nothing is sent. Retried because a scheduler delay of one read can
    // make it count as a disk read, which only ever sends a prefetch.
    [Test]
    [Retry(3)]
    public void Prefetch_MissedSamplesReadAtMemorySpeed_SendNothing()
    {
        RecordingStore accountStore = new(missDelay: TimeSpan.Zero);
        RecordingStore storageStore = new(missDelay: TimeSpan.Zero);
        BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader> reader = new(new BaseFlatPersistence.Reader(accountStore, storageStore));
        Address[] accounts = Accounts(70);
        StorageCell[] slots = accounts.Select(static (address, i) => new StorageCell(address, (UInt256)i)).ToArray();
        long skipped = Nethermind.Db.Metrics.PrefetchesSkippedCached;

        PlanAndPrefetch(reader, accounts, slots, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountStore.ReadKeys, Has.Count.EqualTo(8).And.EquivalentTo(accountStore.ProbedKeys));
            Assert.That(storageStore.ReadKeys, Has.Count.EqualTo(8).And.EquivalentTo(storageStore.ProbedKeys));
            Assert.That(accountStore.PrefetchedKeys, Is.Empty);
            Assert.That(storageStore.PrefetchedKeys, Is.Empty);
            Assert.That(Nethermind.Db.Metrics.PrefetchesSkippedCached - skipped, Is.EqualTo(1));
        }
    }

    // One slow read among fast misses is taken as noise, a scheduler delay or a large page-cache block: no prefetch.
    [Test]
    [Retry(3)]
    public void Prefetch_OneSlowReadAmongFastMisses_SendsNothing()
    {
        const int count = 70;
        Address[] accounts = Accounts(count);
        Span<byte> key = stackalloc byte[BaseFlatPersistence.AccountKeyLength];
        // 35 is the fifth of the eight sample indexes for 70 keys.
        string slowKey = Convert.ToHexString(BaseFlatPersistence.EncodeAccountKeyHashed(key, accounts[35].ToAccountPath));
        RecordingStore accountStore = new(missDelay: TimeSpan.Zero, slowKey: slowKey);
        BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader> reader = new(new BaseFlatPersistence.Reader(accountStore, new RecordingStore()));

        PlanAndPrefetch(reader, accounts, [], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountStore.ReadKeys, Has.Count.EqualTo(BasePersistence.PrefetchSamples));
            Assert.That(accountStore.ReadKeys, Does.Contain(slowKey));
            Assert.That(accountStore.PrefetchedKeys, Is.Empty);
        }
    }

    // Two misses read at disk speed decide disk: the prefetch is sent and later samples are not read.
    [Test]
    public void Prefetch_TwoMissesReadAtDiskSpeed_SendThePrefetch()
    {
        RecordingStore accountStore = new();
        BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader> reader = new(new BaseFlatPersistence.Reader(accountStore, new RecordingStore()));
        Address[] accounts = Accounts(70);

        PlanAndPrefetch(reader, accounts, [], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountStore.ReadKeys, Has.Count.EqualTo(2));
            Assert.That(accountStore.PrefetchedKeys, Has.Count.EqualTo(accounts.Length));
        }
    }

    // A block whose only miss is slow decides disk: there is no second miss to confirm it.
    [Test]
    public void Prefetch_OnlyMissReadAtDiskSpeed_SendsThePrefetch()
    {
        Address[] accounts = Accounts(70);
        Span<byte> key = stackalloc byte[BaseFlatPersistence.AccountKeyLength];
        string coldKey = Convert.ToHexString(BaseFlatPersistence.EncodeAccountKeyHashed(key, accounts[35].ToAccountPath));
        RecordingStore accountStore = new(cached: k => k != coldKey);
        BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader> reader = new(new BaseFlatPersistence.Reader(accountStore, new RecordingStore()));

        PlanAndPrefetch(reader, accounts, [], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountStore.ReadKeys, Is.EqualTo(new[] { coldKey }));
            Assert.That(accountStore.PrefetchedKeys, Has.Count.EqualTo(accounts.Length));
        }
    }

    // After one disk read, a column trusts the next misses to be disk reads: the next RecheckInterval blocks send
    // the prefetch without a timed read, and the block after them is timed again.
    [Test]
    public void Prefetch_AfterADiskRead_SendsWithoutTimingUntilTheRecheck()
    {
        RecordingStore accountStore = new();
        PrefetchSampling sampling = new();
        BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader> reader = new(
            new BaseFlatPersistence.Reader(accountStore, new RecordingStore()), sampling: sampling);
        Address[] accounts = Accounts(70);
        List<int> timedReads = [];

        for (int block = 0; block < PrefetchSampling.RecheckInterval + 2; block++)
        {
            accountStore.ReadKeys.Clear();
            int prefetchedBefore = accountStore.PrefetchedKeys.Count;
            PlanAndPrefetch(reader, accounts, [], CancellationToken.None);
            Assert.That(accountStore.PrefetchedKeys.Count - prefetchedBefore, Is.EqualTo(accounts.Length), $"block {block}");
            timedReads.Add(accountStore.ReadKeys.Count);
        }

        int[] expected = new int[PrefetchSampling.RecheckInterval + 2];
        expected[0] = 2;
        expected[PrefetchSampling.RecheckInterval + 1] = 2;
        Assert.That(timedReads, Is.EqualTo(expected));
    }

    [Test]
    public void PrefetchSampling_TrustsDiskReadsForTheRecheckIntervalPerColumn()
    {
        PrefetchSampling sampling = new();

        Assert.That(sampling.Accounts.TrustMissAsDiskRead(), Is.False);
        sampling.Accounts.RecordDiskRead();
        int trusted = 0;
        while (sampling.Accounts.TrustMissAsDiskRead()) trusted++;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trusted, Is.EqualTo(PrefetchSampling.RecheckInterval));
            Assert.That(sampling.Slots.TrustMissAsDiskRead(), Is.False);
        }
    }

    // After a block whose misses were read from the page cache, the next RecheckInterval blocks skip the decision
    // without a probe or a read, and the block after them decides again.
    [Test]
    [Retry(3)]
    public void Plan_AfterPageCacheReads_SkipsWithoutProbesUntilTheRecheck()
    {
        RecordingStore accountStore = new(missDelay: TimeSpan.Zero);
        PrefetchSampling sampling = new();
        BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader> reader = new(
            new BaseFlatPersistence.Reader(accountStore, new RecordingStore()), sampling: sampling);
        Address[] accounts = Accounts(70);
        List<int> probes = [];

        for (int block = 0; block < PrefetchSampling.RecheckInterval + 2; block++)
        {
            accountStore.ProbedKeys.Clear();
            PlanAndPrefetch(reader, accounts, [], CancellationToken.None);
            probes.Add(accountStore.ProbedKeys.Count);
        }

        int[] expected = new int[PrefetchSampling.RecheckInterval + 2];
        expected[0] = BasePersistence.PrefetchSamples;
        expected[PrefetchSampling.RecheckInterval + 1] = BasePersistence.PrefetchSamples;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(probes, Is.EqualTo(expected));
            Assert.That(accountStore.PrefetchedKeys, Is.Empty);
        }
    }

    // While blocks are fully cached the next block probes two samples; a miss among them restores all eight.
    [Test]
    public void Plan_AfterAFullyCachedBlock_ProbesTwoSamples()
    {
        bool cold = false;
        RecordingStore accountStore = new(cached: _ => !cold);
        PrefetchSampling sampling = new();
        BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader> reader = new(
            new BaseFlatPersistence.Reader(accountStore, new RecordingStore()), sampling: sampling);
        Address[] accounts = Accounts(70);
        List<int> probes = [];

        for (int block = 0; block < 4; block++)
        {
            cold = block == 2;
            accountStore.ProbedKeys.Clear();
            PlanAndPrefetch(reader, accounts, [], CancellationToken.None);
            probes.Add(accountStore.ProbedKeys.Count);
        }

        using (Assert.EnterMultipleScope())
        {
            // Cached, cached run, a cold block confirmed by both of its two probes, then all eight again.
            Assert.That(probes, Is.EqualTo(new[] { 8, 2, 2, 8 }));
            Assert.That(accountStore.PrefetchedKeys, Has.Count.EqualTo(accounts.Length));
        }
    }

    [Test]
    public void IsDiskRead_CountsOnlyReadsLongerThanTheMemoryLimit()
    {
        long limit = BasePersistence.MemoryReadLimitTicks;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(limit, Is.EqualTo((long)(BasePersistence.MemoryReadLimit.TotalSeconds * Stopwatch.Frequency)));
            Assert.That(BasePersistence.IsDiskRead(1_000, 1_000), Is.False);
            Assert.That(BasePersistence.IsDiskRead(1_000, 1_000 + limit), Is.False);
            Assert.That(BasePersistence.IsDiskRead(1_000, 1_000 + limit + 1), Is.True);
        }
    }

    // A cached block costs at most eight probes per column, each at a distinct key, whatever the key count.
    [TestCase(1, 1)]
    [TestCase(7, 7)]
    [TestCase(8, 8)]
    [TestCase(9, 8)]
    [TestCase(15, 8)]
    [TestCase(16, 8)]
    [TestCase(70, 8)]
    [TestCase(10_000, 8)]
    public void Prefetch_ProbesAtMostEightDistinctKeysPerColumn(int count, int expectedProbes)
    {
        RecordingStore accountStore = new(cached: static _ => true);
        RecordingStore storageStore = new(cached: static _ => true);
        BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader> reader = new(new BaseFlatPersistence.Reader(accountStore, storageStore));
        Address[] accounts = Accounts(count);
        StorageCell[] slots = accounts.Select(static (address, i) => new StorageCell(address, (UInt256)i)).ToArray();

        PlanAndPrefetch(reader, accounts, slots, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountStore.ProbedKeys, Has.Count.EqualTo(expectedProbes).And.Unique);
            Assert.That(storageStore.ProbedKeys, Has.Count.EqualTo(expectedProbes).And.Unique);
            Assert.That(accountStore.PrefetchedKeys, Is.Empty);
            Assert.That(storageStore.PrefetchedKeys, Is.Empty);
        }
    }

    // A cancelled prefetch sends nothing; the plan before it probes at most its samples.
    [Test]
    public void Prefetch_WithACancelledToken_SendsNothing()
    {
        RecordingStore accountStore = new();
        RecordingStore storageStore = new();
        BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader> reader = new(new BaseFlatPersistence.Reader(accountStore, storageStore));
        Address[] accounts = Accounts(70);

        PlanAndPrefetch(reader, accounts, [new StorageCell(accounts[0], 1)], new CancellationToken(canceled: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountStore.ProbedKeys, Has.Count.LessThanOrEqualTo(BasePersistence.PrefetchSamples));
            Assert.That(accountStore.PrefetchBatches, Is.Empty);
            Assert.That(storageStore.PrefetchBatches, Is.Empty);
        }
    }

    // Cancellation during submission stops before the next 64-key batch; the batch already sent stays sent.
    [Test]
    public void Prefetch_CancelledDuringABatch_StartsNoFurtherBatch()
    {
        using CancellationTokenSource cancellation = new();
        RecordingStore accountStore = new(onPrefetch: cancellation.Cancel);
        RecordingStore storageStore = new();
        BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader> reader = new(new BaseFlatPersistence.Reader(accountStore, storageStore));
        Address[] accounts = Accounts(200);

        PlanAndPrefetch(reader, accounts, [new StorageCell(accounts[0], 1)], cancellation.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountStore.PrefetchBatches, Is.EqualTo(new[] { 64 }));
            Assert.That(storageStore.PrefetchBatches, Is.Empty);
        }
    }

    /// <summary>What the scope does: plans on samples spread over the keys, then sends the columns the plan chose.</summary>
    private static void PlanAndPrefetch(BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader> reader, Address[] accounts, StorageCell[] slots,
        CancellationToken cancellationToken)
    {
        Address[] accountSamples = new Address[BasePersistence.PrefetchSamples];
        StorageCell[] slotSamples = new StorageCell[BasePersistence.PrefetchSamples];
        int accountCount = BasePersistence.FillSamples<Address>(accounts, accountSamples);
        int slotCount = BasePersistence.FillSamples<StorageCell>(slots, slotSamples);
        IPersistence.PrefetchColumns columns = reader.PlanPrefetch(accountSamples.AsSpan(0, accountCount), slotSamples.AsSpan(0, slotCount));
        reader.Prefetch(
            (columns & IPersistence.PrefetchColumns.Accounts) != 0 ? accounts : default,
            (columns & IPersistence.PrefetchColumns.Slots) != 0 ? slots : default,
            cancellationToken);
    }

    private static Address[] Accounts(int count)
    {
        Address[] accounts = new Address[count];
        for (int i = 0; i < count; i++) accounts[i] = Address.FromNumber((UInt256)(i + 1));
        return accounts;
    }

    /// <summary>
    /// Records the keys of scalar reads and prefetches; every read misses. A read of a key that is not cached takes
    /// <paramref name="missDelay"/>, 1 ms by default, as a disk read does; <paramref name="slowKey"/> always takes 1 ms.
    /// </summary>
    private sealed class RecordingStore(Func<string, bool>? cached = null, Action? onPrefetch = null, TimeSpan? missDelay = null, string? slowKey = null)
        : ISortedKeyValueStore, IReadPrefetcher
    {
        private static readonly TimeSpan DiskRead = TimeSpan.FromMilliseconds(1);

        public List<string> ReadKeys { get; } = [];
        public List<string> ProbedKeys { get; } = [];
        public List<string> PrefetchedKeys { get; } = [];
        public List<int> PrefetchBatches { get; } = [];

        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            Read(key);
            return null;
        }

        public int Get(scoped ReadOnlySpan<byte> key, Span<byte> output, ReadFlags flags = ReadFlags.None)
        {
            Read(key);
            return 0;
        }

        private void Read(ReadOnlySpan<byte> key)
        {
            string hex = Convert.ToHexString(key);
            ReadKeys.Add(hex);
            TimeSpan delay = hex == slowKey ? DiskRead : cached?.Invoke(hex) == true ? TimeSpan.Zero : missDelay ?? DiskRead;
            if (delay > TimeSpan.Zero) Thread.Sleep(delay);
        }

        public bool IsCached(ReadOnlySpan<byte> key)
        {
            string hex = Convert.ToHexString(key);
            ProbedKeys.Add(hex);
            return cached?.Invoke(hex) ?? false;
        }

        public void Prefetch(ReadOnlySpan<byte> keys, int keyLength)
        {
            PrefetchBatches.Add(keys.Length / keyLength);
            for (int offset = 0; offset < keys.Length; offset += keyLength)
                PrefetchedKeys.Add(Convert.ToHexString(keys.Slice(offset, keyLength)));
            onPrefetch?.Invoke();
        }

        public byte[]? FirstKey => null;
        public byte[]? LastKey => null;
        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None) =>
            throw new NotSupportedException();
    }

    /// <summary>Returns the same value for any key; enough to exercise <c>TryGetStorage</c>'s decode path.</summary>
    private sealed class FixedValueStore(byte[] value) : ISortedKeyValueStore
    {
        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => value;
        public byte[]? FirstKey => null;
        public byte[]? LastKey => null;
        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None) =>
            throw new NotSupportedException();
    }
}
