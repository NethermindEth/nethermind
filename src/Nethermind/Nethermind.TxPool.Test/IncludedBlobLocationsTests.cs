// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

[TestFixture]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class IncludedBlobLocationsTests
{
    private const ulong MaxBlockDistance = 4;

    private readonly IncludedBlobLocations _locations = new(MaxBlockDistance);

    public enum Prune
    {
        Finality,
        Distance,
    }

    [TestCase(1, 0, 0)]
    [TestCase(2, 0, 1)]
    [TestCase(3, 1, 0)]
    public void Lookup_returns_the_recorded_location(int hashSeed, int expectedPosition, int expectedBlobIndex)
    {
        _locations.Record(10, null, [BlobTx(1, 2), BlobTx(3)]);

        bool found = _locations.TryGetLocation(Hash(hashSeed), out IncludedBlobLocation location);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(location, Is.EqualTo(new IncludedBlobLocation(10, expectedPosition, expectedBlobIndex)));
        }
    }

    [Test]
    public void Lookup_misses_a_hash_that_was_not_recorded([Values(32, 0, 31, 33)] int hashLength)
    {
        _locations.Record(10, null, [BlobTx(1)]);
        byte[] hash = new byte[hashLength];
        hash.AsSpan().Fill(0xff);

        Assert.That(_locations.TryGetLocation(hash, out _), Is.False);
    }

    [Test]
    public void Recording_a_block_drops_the_batches_at_its_height_and_above([Values(10UL, 12UL, 13UL, 14UL)] ulong reRecordedHeight)
    {
        for (ulong height = 10; height <= 13; height++)
        {
            _locations.Record(height, null, [BlobTx((int)height)]);
        }

        _locations.Record(reRecordedHeight, null, [BlobTx(100)]);

        using (Assert.EnterMultipleScope())
        {
            for (ulong height = 10; height <= 13; height++)
            {
                Assert.That(_locations.TryGetLocation(Hash((int)height), out _), Is.EqualTo(height < reRecordedHeight), $"height {height}");
            }

            Assert.That(_locations.TryGetLocation(Hash(100), out IncludedBlobLocation location), Is.True, "re-recorded block");
            Assert.That(location, Is.EqualTo(new IncludedBlobLocation(reRecordedHeight, 0, 0)));
        }
    }

    [TestCase(11UL, true)]
    [TestCase(10UL + MaxBlockDistance, true)]
    [TestCase(10UL + MaxBlockDistance + 1, false)]
    [TestCase(1000UL, false)]
    public void Batches_further_than_the_max_block_distance_below_the_recorded_block_are_dropped(ulong nextHeight, bool expectedFound)
    {
        _locations.Record(10, null, [BlobTx(1)]);

        _locations.Record(nextHeight, null, [BlobTx(2)]);

        Assert.That(_locations.TryGetLocation(Hash(1), out _), Is.EqualTo(expectedFound));
    }

    [Test]
    public void Finality_prune_drops_the_batches_at_and_below_the_finalized_block([Values(9UL, 11UL, 13UL, 20UL)] ulong finalizedBlockNumber)
    {
        for (ulong height = 10; height <= 13; height++)
        {
            _locations.Record(height, null, [BlobTx((int)height)]);
        }

        _locations.PruneFinalized(finalizedBlockNumber);

        using (Assert.EnterMultipleScope())
        {
            for (ulong height = 10; height <= 13; height++)
            {
                Assert.That(_locations.TryGetLocation(Hash((int)height), out _), Is.EqualTo(height > finalizedBlockNumber), $"height {height}");
            }
        }
    }

    [Test]
    public void A_block_recorded_after_a_finality_prune_beyond_the_head_is_indexed()
    {
        _locations.Record(10, null, [BlobTx(1)]);
        _locations.PruneFinalized(20);
        int countAfterPrune = _locations.Count;

        _locations.Record(11, null, [BlobTx(2)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(countAfterPrune, Is.Zero);
            Assert.That(_locations.TryGetLocation(Hash(2), out IncludedBlobLocation location), Is.True);
            Assert.That(location, Is.EqualTo(new IncludedBlobLocation(11, 0, 0)));
        }
    }

    [Test]
    public void A_hash_recorded_again_by_a_later_block_survives_pruning_of_the_earlier_block([Values] Prune prune)
    {
        _locations.Record(10, null, [BlobTx(1)]);
        _locations.Record(12, null, [BlobTx(2), BlobTx(1)]);

        PruneBlock10(prune);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_locations.TryGetLocation(Hash(1), out IncludedBlobLocation location), Is.True);
            Assert.That(location, Is.EqualTo(new IncludedBlobLocation(12, 1, 0)));
        }
    }

    [Test]
    public void A_hash_recorded_twice_by_one_block_is_removed_with_it([Values] Prune prune)
    {
        _locations.Record(10, null, [BlobTx(1), BlobTx(1)]);

        PruneBlock10(prune);

        Assert.That(_locations.Count, Is.Zero);
    }

    [Test]
    public void Recording_nothing_keeps_the_batch_of_the_same_block_and_drops_the_heights_above([Values(10UL, 11UL)] ulong height, [Values] bool sameBlock)
    {
        for (ulong h = 10; h <= 12; h++)
        {
            _locations.Record(h, BlockHash((int)h), [BlobTx((int)h)]);
        }

        _locations.Record(height, BlockHash(sameBlock ? (int)height : 99), []);

        using (Assert.EnterMultipleScope())
        {
            for (ulong h = 10; h <= 12; h++)
            {
                bool expectedKept = h < height || (h == height && sameBlock);
                Assert.That(_locations.TryGetLocation(Hash((int)h), out IncludedBlobLocation location), Is.EqualTo(expectedKept), $"height {h}");
                if (expectedKept)
                {
                    Assert.That(location, Is.EqualTo(new IncludedBlobLocation(h, 0, 0)), $"location at height {h}");
                }
            }
        }
    }

    [Test]
    public void Recording_blobs_for_the_same_block_replaces_its_batch()
    {
        Hash256 hash = BlockHash(1);
        _locations.Record(10, hash, [BlobTx(1), BlobTx(2)]);

        _locations.Record(10, hash, [BlobTx(2)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_locations.TryGetLocation(Hash(1), out _), Is.False);
            Assert.That(_locations.TryGetLocation(Hash(2), out IncludedBlobLocation location), Is.True);
            Assert.That(location, Is.EqualTo(new IncludedBlobLocation(10, 0, 0)));
        }
    }

    [Test]
    public void A_kept_batch_is_still_dropped_by_pruning()
    {
        Hash256 hash = BlockHash(1);
        _locations.Record(10, hash, [BlobTx(1)]);
        _locations.Record(10, hash, []);

        _locations.PruneFinalized(10);

        Assert.That(_locations.Count, Is.Zero);
    }

    [TestCase(10UL, false)]
    [TestCase(11UL, true)]
    public void An_empty_batch_still_supersedes_and_records_nothing(ulong emptyBlockHeight, bool expectedFound)
    {
        _locations.Record(10, null, [BlobTx(1)]);

        _locations.Record(emptyBlockHeight, null, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_locations.TryGetLocation(Hash(1), out _), Is.EqualTo(expectedFound));
            Assert.That(_locations.Count, Is.EqualTo(expectedFound ? 1 : 0));
        }
    }

    [Test]
    public void Hashes_missing_or_of_the_wrong_length_are_not_indexed()
    {
        Transaction tx = new() { Type = TxType.Blob, BlobVersionedHashes = [null, new byte[31], Hash(1)] };

        _locations.Record(10, null, [tx, new Transaction()]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_locations.Count, Is.EqualTo(1));
            Assert.That(_locations.TryGetLocation(Hash(1), out IncludedBlobLocation location), Is.True);
            Assert.That(location, Is.EqualTo(new IncludedBlobLocation(10, 0, 2)));
        }
    }

    [Test]
    public void Entry_count_never_exceeds_the_window_times_the_entries_per_block([Values(0UL, MaxBlockDistance, 64UL)] ulong maxBlockDistance)
    {
        const int maxEntriesPerBlock = 6;
        IncludedBlobLocations locations = new(maxBlockDistance);
        Random random = new(42);
        ulong head = 1;
        ulong maxCount = 0;
        int hashSeed = 0;

        for (int step = 0; step < 2_000; step++)
        {
            // Mostly advance; sometimes reorg a few blocks back; never finalize, so only the distance cap bounds the index.
            head = random.Next(8) == 0 ? head.SaturatingSub((ulong)random.Next(4)) : head + 1;
            locations.Record(head, null, CreateBlock(maxEntriesPerBlock, ref hashSeed, random));
            maxCount = Math.Max(maxCount, (ulong)locations.Count);
        }

        Assert.That(maxCount, Is.LessThanOrEqualTo((maxBlockDistance + 1) * maxEntriesPerBlock));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Concurrent_readers_only_see_locations_that_were_recorded_for_the_hash()
    {
        const int hashCount = 16;
        const int steps = 20_000;
        (ulong Height, Transaction[] Txs)[] schedule = CreateSchedule(steps, hashCount);
        ConcurrentDictionary<(int Seed, IncludedBlobLocation Location), bool> recorded = new();
        foreach ((ulong height, Transaction[] txs) in schedule)
        {
            for (int position = 0; position < txs.Length; position++)
            {
                byte[][] hashes = txs[position].BlobVersionedHashes;
                for (int blobIndex = 0; blobIndex < hashes.Length; blobIndex++)
                {
                    recorded[(Seed(hashes[blobIndex]), new IncludedBlobLocation(height, position, blobIndex))] = true;
                }
            }
        }

        using CancellationTokenSource writerDone = new();
        ConcurrentBag<string> violations = [];
        long reads = 0;
        Task[] readers = new Task[4];
        for (int r = 0; r < readers.Length; r++)
        {
            readers[r] = Task.Run(() =>
            {
                // At least one pass, even when the writer finishes before this reader is scheduled.
                do
                {
                    for (int seed = 0; seed < hashCount; seed++)
                    {
                        if (_locations.TryGetLocation(Hash(seed), out IncludedBlobLocation location) && !recorded.ContainsKey((seed, location)))
                        {
                            violations.Add($"hash {seed} at {location}");
                        }

                        Interlocked.Increment(ref reads);
                    }
                } while (!writerDone.IsCancellationRequested);
            });
        }

        Task writer = Task.Run(() =>
        {
            try
            {
                foreach ((ulong height, Transaction[] txs) in schedule)
                {
                    _locations.Record(height, null, txs);
                    if (height % 7 == 0)
                    {
                        _locations.PruneFinalized(height - 3);
                    }
                }
            }
            finally
            {
                writerDone.Cancel();
            }
        });

        await Task.WhenAll([writer, .. readers]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(violations, Is.Empty);
            Assert.That(Interlocked.Read(ref reads), Is.GreaterThan(0));
        }
    }

    private void PruneBlock10(Prune prune)
    {
        if (prune == Prune.Finality)
        {
            _locations.PruneFinalized(10);
        }
        else
        {
            _locations.Record(11 + MaxBlockDistance, null, []);
        }
    }

    private static (ulong Height, Transaction[] Txs)[] CreateSchedule(int steps, int hashCount)
    {
        Random random = new(7);
        (ulong, Transaction[])[] schedule = new (ulong, Transaction[])[steps];
        ulong head = 1;
        for (int step = 0; step < steps; step++)
        {
            head = random.Next(6) == 0 ? head.SaturatingSub((ulong)random.Next(3)) : head + 1;
            Transaction[] txs = new Transaction[random.Next(4)];
            for (int position = 0; position < txs.Length; position++)
            {
                // Few distinct hashes, so the same hash is recorded by many blocks and positions.
                txs[position] = BlobTx(random.Next(hashCount), random.Next(hashCount));
            }

            schedule[step] = (head, txs);
        }

        return schedule;
    }

    private static Transaction[] CreateBlock(int maxEntriesPerBlock, ref int hashSeed, Random random)
    {
        int remaining = random.Next(maxEntriesPerBlock + 1);
        List<Transaction> txs = [];
        while (remaining > 0)
        {
            int blobCount = Math.Min(remaining, random.Next(1, 4));
            int[] seeds = new int[blobCount];
            for (int i = 0; i < blobCount; i++)
            {
                seeds[i] = hashSeed++;
            }

            txs.Add(BlobTx(seeds));
            remaining -= blobCount;
        }

        return [.. txs];
    }

    private static Transaction BlobTx(params int[] hashSeeds)
    {
        byte[][] hashes = new byte[hashSeeds.Length][];
        for (int i = 0; i < hashSeeds.Length; i++)
        {
            hashes[i] = Hash(hashSeeds[i]);
        }

        return new Transaction { Type = TxType.Blob, BlobVersionedHashes = hashes };
    }

    private static Hash256 BlockHash(int seed) => new(Hash(seed));

    private static byte[] Hash(int seed)
    {
        byte[] hash = new byte[32];
        hash[0] = 0x01;
        BinaryPrimitives.WriteInt32BigEndian(hash.AsSpan(1), seed);
        return hash;
    }

    private static int Seed(byte[] hash) => BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(1));
}
