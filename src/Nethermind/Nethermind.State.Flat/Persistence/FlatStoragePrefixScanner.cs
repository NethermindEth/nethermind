// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// Groups the flat <c>Storage</c> column by the 4-byte address prefix of its keys and reports the addresses that
/// share a prefix, how big their storage is, and how much extra scanning a collision costs.
/// </summary>
/// <remarks>
/// A storage key is <c>addr[0..4] | slot(32) | addr[4..20]</c> (see <see cref="BaseFlatPersistence"/>). Point lookups
/// build the full key and are unaffected by collisions, but every range operation —
/// <see cref="BaseFlatPersistence.Reader.CreateStorageIterator"/>, <see cref="BaseFlatPersistence.WriteBatch.SelfDestruct"/>
/// and <see cref="BaseFlatPersistence.WriteBatch.DeleteStorageRange"/> — can only seek on the 4-byte prefix and then
/// discards the entries whose 16-byte suffix belongs to another address, one at a time.
/// In <see cref="Nethermind.Db.FlatLayout.PreimageFlat"/> the prefix is the first 4 bytes of the address itself, which
/// can be mined for vanity leading bytes, so several large contracts may end up in one bucket. On a hashed layout the
/// same scan measures the uniform-random baseline.
/// </remarks>
public static class FlatStoragePrefixScanner
{
    private const int StoragePrefixPortion = BaseFlatPersistence.StoragePrefixPortion;
    private const int StorageKeyLength = BaseFlatPersistence.StorageKeyLength;
    private const int SuffixOffset = StoragePrefixPortion + BaseFlatPersistence.StorageSlotKeySize;

    /// <summary>How many slots to read between cancellation checks and progress logs.</summary>
    private const long ProgressInterval = 10_000_000;

    private static readonly string[] HistogramLabels = ["1", "2", "3", "4", "5-8", "9-16", "17+"];

    /// <summary>Streams the whole storage column once, bucketing entries by their 4-byte key prefix.</summary>
    /// <param name="storage">The flat <c>Storage</c> column.</param>
    /// <param name="topCount">How many colliding prefixes and largest addresses to keep in the report.</param>
    /// <param name="logger">Receives periodic progress.</param>
    /// <param name="cancellationToken">When cancelled, the scan stops early and the report is marked partial.</param>
    /// <remarks>
    /// Keys sort by prefix first, so a bucket's entries are contiguous and only one bucket is held in memory at a time.
    /// </remarks>
    public static Report Scan(ISortedKeyValueStore storage, int topCount, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topCount);

        Report report = new();
        // Min-heaps: once over capacity the smallest score is dropped, so memory stays bound to topCount.
        PriorityQueue<PrefixStats, long> topPrefixes = new();
        PriorityQueue<AddressStats, long> topAddresses = new();
        Dictionary<UInt128, SlotTotals> bucket = [];
        uint bucketPrefix = 0;

        void FlushBucket()
        {
            if (bucket.Count == 0) return;

            long bucketSlots = 0;
            long bucketValueBytes = 0;
            foreach (SlotTotals totals in bucket.Values)
            {
                bucketSlots += totals.SlotCount;
                bucketValueBytes += totals.ValueBytes;
            }

            List<AddressStats> addresses = new(bucket.Count);
            foreach ((UInt128 suffix, SlotTotals totals) in bucket)
            {
                AddressStats address = new(ToAddressKey(bucketPrefix, suffix), totals.SlotCount, totals.ValueBytes, bucketSlots);
                addresses.Add(address);
                KeepTop(topAddresses, address, address.SlotCount, topCount);
            }
            addresses.Sort(static (left, right) => right.SlotCount.CompareTo(left.SlotCount));

            report.DistinctPrefixes++;
            report.DistinctAddresses += bucket.Count;
            report.AddressesPerPrefix[HistogramBucket(bucket.Count)]++;

            if (bucket.Count > 1)
            {
                report.CollidingPrefixes++;
                report.AddressesInCollidingPrefixes += bucket.Count;
                report.SlotsInCollidingPrefixes += bucketSlots;
                KeepTop(topPrefixes, new PrefixStats(bucketPrefix, bucketSlots, bucketValueBytes, addresses), bucketSlots, topCount);
            }

            bucket.Clear();
        }

        Span<byte> firstKey = stackalloc byte[StorageKeyLength];
        Span<byte> lastKey = stackalloc byte[StorageKeyLength + 1];
        lastKey.Fill(0xff);

        long startTimestamp = Stopwatch.GetTimestamp();
        long untilNextCheck = ProgressInterval;

        using (ISortedView view = storage.GetViewBetween(firstKey, lastKey))
        {
            while (view.MoveNext())
            {
                ReadOnlySpan<byte> key = view.CurrentKey;
                if (key.Length != StorageKeyLength)
                {
                    report.SkippedKeys++;
                    continue;
                }

                uint prefix = BinaryPrimitives.ReadUInt32BigEndian(key);
                if (prefix != bucketPrefix)
                {
                    FlushBucket();
                    bucketPrefix = prefix;
                }

                ref SlotTotals totals = ref CollectionsMarshal.GetValueRefOrAddDefault(bucket, ReadSuffix(key), out _);
                totals.SlotCount++;
                totals.ValueBytes += view.CurrentValue.Length;

                report.TotalSlots++;
                report.TotalValueBytes += view.CurrentValue.Length;

                if (--untilNextCheck == 0)
                {
                    untilNextCheck = ProgressInterval;
                    if (cancellationToken.IsCancellationRequested)
                    {
                        report.Completed = false;
                        break;
                    }

                    if (logger.IsInfo)
                        logger.Info($"Flat storage prefix scan: {report.TotalSlots:N0} slots, {report.DistinctPrefixes:N0} prefixes in {Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds:N0}s");
                }
            }
        }

        FlushBucket();

        report.TopCollidingPrefixes = DrainDescending(topPrefixes);
        report.TopAddresses = DrainDescending(topAddresses);
        report.Elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        return report;
    }

    private static UInt128 ReadSuffix(ReadOnlySpan<byte> key) => new(
        BinaryPrimitives.ReadUInt64BigEndian(key[SuffixOffset..]),
        BinaryPrimitives.ReadUInt64BigEndian(key[(SuffixOffset + sizeof(ulong))..]));

    private static byte[] ToAddressKey(uint prefix, UInt128 suffix)
    {
        byte[] addressKey = new byte[Address.Size];
        BinaryPrimitives.WriteUInt32BigEndian(addressKey, prefix);
        BinaryPrimitives.WriteUInt64BigEndian(addressKey.AsSpan(StoragePrefixPortion), (ulong)(suffix >> 64));
        BinaryPrimitives.WriteUInt64BigEndian(addressKey.AsSpan(StoragePrefixPortion + sizeof(ulong)), (ulong)suffix);
        return addressKey;
    }

    private static int HistogramBucket(int addressCount) => addressCount switch
    {
        <= 4 => addressCount - 1,
        <= 8 => 4,
        <= 16 => 5,
        _ => 6
    };

    private static void KeepTop<T>(PriorityQueue<T, long> queue, T item, long score, int capacity)
    {
        queue.Enqueue(item, score);
        if (queue.Count > capacity) queue.Dequeue();
    }

    private static IReadOnlyList<T> DrainDescending<T>(PriorityQueue<T, long> queue) where T : struct
    {
        List<T> items = new(queue.Count);
        while (queue.TryDequeue(out T item, out long _)) items.Add(item);
        items.Reverse();
        return items;
    }

    private struct SlotTotals
    {
        public long SlotCount;
        public long ValueBytes;
    }

    /// <param name="AddressKey">The 20-byte key portion — the address itself in preimage mode, its hash prefix otherwise.</param>
    /// <param name="PrefixSlotCount">Slots stored under the whole 4-byte prefix, including the other addresses in it.</param>
    public readonly record struct AddressStats(byte[] AddressKey, long SlotCount, long ValueBytes, long PrefixSlotCount)
    {
        /// <summary>Entries a full iteration or self-destruct over this address reads, per entry that belongs to it.</summary>
        public double ScanAmplification => (double)PrefixSlotCount / SlotCount;
    }

    /// <param name="Addresses">The addresses sharing this prefix, largest first.</param>
    public readonly record struct PrefixStats(uint Prefix, long SlotCount, long ValueBytes, IReadOnlyList<AddressStats> Addresses);

    public sealed class Report
    {
        public long TotalSlots { get; internal set; }
        public long TotalValueBytes { get; internal set; }

        /// <summary>Keys that are not <see cref="StorageKeyLength"/> bytes long and were left out of the report.</summary>
        public long SkippedKeys { get; internal set; }

        public long DistinctPrefixes { get; internal set; }
        public long DistinctAddresses { get; internal set; }
        public long CollidingPrefixes { get; internal set; }
        public long AddressesInCollidingPrefixes { get; internal set; }
        public long SlotsInCollidingPrefixes { get; internal set; }

        /// <summary>Prefix counts bucketed by how many addresses share them, see <see cref="HistogramLabels"/>.</summary>
        public long[] AddressesPerPrefix { get; } = new long[HistogramLabels.Length];

        public IReadOnlyList<PrefixStats> TopCollidingPrefixes { get; internal set; } = [];
        public IReadOnlyList<AddressStats> TopAddresses { get; internal set; } = [];
        public TimeSpan Elapsed { get; internal set; }

        /// <summary>False when the scan was cancelled, in which case the numbers only cover the prefixes reached.</summary>
        public bool Completed { get; internal set; } = true;

        public override string ToString()
        {
            StringBuilder builder = new();
            builder.AppendLine($"{(Completed ? "Scanned" : "Cancelled after")} {TotalSlots:N0} slots, {TotalValueBytes:N0} value bytes, {DistinctPrefixes:N0} prefixes, {DistinctAddresses:N0} addresses, {SkippedKeys:N0} keys skipped in {Elapsed.TotalSeconds:N0}s");

            builder.Append("Addresses per prefix:");
            for (int i = 0; i < AddressesPerPrefix.Length; i++)
                builder.Append($" {HistogramLabels[i]}: {AddressesPerPrefix[i]:N0}");
            builder.AppendLine();

            builder.AppendLine($"Colliding prefixes: {CollidingPrefixes:N0} ({Share(CollidingPrefixes, DistinctPrefixes)} of prefixes) holding {AddressesInCollidingPrefixes:N0} addresses and {SlotsInCollidingPrefixes:N0} slots ({Share(SlotsInCollidingPrefixes, TotalSlots)} of slots)");

            builder.AppendLine($"Largest {TopCollidingPrefixes.Count} colliding prefixes:");
            foreach (PrefixStats prefix in TopCollidingPrefixes)
            {
                builder.AppendLine($"  0x{prefix.Prefix:x8}  {prefix.Addresses.Count:N0} addresses  {prefix.SlotCount:N0} slots  {prefix.ValueBytes:N0} bytes");
                foreach (AddressStats address in prefix.Addresses) builder.AppendLine($"    {Describe(address)}");
            }

            builder.AppendLine($"Largest {TopAddresses.Count} addresses:");
            foreach (AddressStats address in TopAddresses) builder.AppendLine($"  {Describe(address)}");

            return builder.ToString();
        }

        private static string Describe(AddressStats address) =>
            $"{address.AddressKey.ToHexString(withZeroX: true)}  {address.SlotCount:N0} slots  {address.ValueBytes:N0} bytes  scan amplification x{address.ScanAmplification:N2}";

        private static string Share(long part, long total) => $"{(total == 0 ? 0 : (double)part * 100 / total):N2}%";
    }
}
