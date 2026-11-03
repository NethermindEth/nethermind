// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db.LogIndex;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NUnit.Framework;
using Testably.Abstractions;

namespace Nethermind.Db.Test.LogIndex;

[Parallelizable(ParallelScope.All)]
public class LogIndexEnumeratorTests
{
    private const int BlockCount = 6_000;
    private const int BatchSize = 50;

    private static readonly Address SparseAddress = TestItem.AddressA;
    private static readonly Address DenseAddress = TestItem.AddressB;
    private static readonly Address MissingAddress = TestItem.AddressC;
    private static readonly Address EarlyAddress = TestItem.AddressE;
    private const int EarlyEnd = 5_000;

    private TempPath _dbPath = null!;
    private LogIndexStorage _storage = null!;
    private int[] _sparseBlocks = null!;
    private int[] _denseBlocks = null!;
    private int[] _earlyBlocks = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        Random random = new(42);
        _sparseBlocks = Enumerable.Range(0, BlockCount).Where(_ => random.Next(100) == 0).ToArray();
        _denseBlocks = Enumerable.Range(0, BlockCount).Where(_ => random.Next(10) != 0).ToArray();
        _earlyBlocks = _denseBlocks.Where(static b => b < EarlyEnd).ToArray();

        _dbPath = TempPath.GetTempDirectory();
        DbConfig dbConfig = new();
        RocksDbConfigFactory configFactory = new(dbConfig, new PruningConfig(), new TestHardwareInfo(0), LimboLogs.Instance);
        RocksDbFactory dbFactory = new(configFactory, dbConfig, new HyperClockCacheWrapper(), new RealFileSystem(), new TestLogManager(), _dbPath.Path);

        _storage = new LogIndexStorage(dbFactory, LimboLogs.Instance, new LogIndexConfig
        {
            Enabled = true,
            CompressionDistance = 16,
            MaxReorgDepth = 200
        });

        // Forward from the middle, then backward to the start, so postings use backward, finalized and forward keys
        HashSet<int> sparse = [.. _sparseBlocks];
        HashSet<int> dense = [.. _denseBlocks];
        // Merging after each batch compresses it into its own chunk, so postings span many chunks
        for (int start = BlockCount / 2; start < BlockCount; start += BatchSize)
        {
            await _storage.AddReceiptsAsync(CreateBatch(start, BatchSize, sparse, dense, descending: false), isBackwardSync: false);
            await _storage.CompactAsync(mergeIterations: 1);
        }

        for (int end = BlockCount / 2; end > 0; end -= BatchSize)
        {
            await _storage.AddReceiptsAsync(CreateBatch(end - BatchSize, BatchSize, sparse, dense, descending: true), isBackwardSync: true);
            await _storage.CompactAsync(mergeIterations: 1);
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        try
        {
            await _storage.StopAsync();
        }
        finally
        {
            await ((IAsyncDisposable)_storage).DisposeAsync();
        }

        _dbPath.Dispose();
    }

    [TestCase(false, 0, BlockCount - 1)]
    [TestCase(true, 0, BlockCount - 1)]
    [TestCase(false, 777, 4_321)]
    [TestCase(true, 777, 4_321)]
    [TestCase(true, BlockCount / 2 - 5, BlockCount / 2 + 5)]
    public void MoveTo_matches_sequential_scan(bool dense, int from, int to)
    {
        Address address = dense ? DenseAddress : SparseAddress;
        int[] expected = (dense ? _denseBlocks : _sparseBlocks).Where(b => b >= from && b <= to).ToArray();
        Assert.That(expected, Is.Not.Empty, "Unreliable test: needs blocks in range.");

        Random random = new(from ^ to ^ (dense ? 1 : 0));
        for (int run = 0; run < 50; run++)
        {
            using IEnumerator<int> enumerator = _storage.GetEnumerator(address, from, to);
            ILogIndexEnumerator seekable = (ILogIndexEnumerator)enumerator;

            Assert.That((enumerator.MoveNext(), enumerator.Current), Is.EqualTo((true, expected[0])));

            int index = 0;
            while (true)
            {
                bool moved;
                if (random.Next(4) == 0)
                {
                    moved = enumerator.MoveNext();
                    index++;
                }
                else
                {
                    int target = enumerator.Current + NextGap(random);
                    moved = seekable.MoveTo(target);
                    int found = Array.BinarySearch(expected, index, expected.Length - index, target);
                    index = found >= 0 ? found : ~found;
                }

                if (index >= expected.Length)
                {
                    Assert.That((moved, seekable.MoveTo(to)), Is.EqualTo((false, false)), "a completed enumerator stays completed");
                    break;
                }

                Assert.That((moved, enumerator.Current), Is.EqualTo((true, expected[index])));
            }
        }
    }

    [Test]
    public void MoveTo_does_not_move_when_current_reaches_target()
    {
        using IEnumerator<int> enumerator = _storage.GetEnumerator(DenseAddress, 0, BlockCount - 1);
        ILogIndexEnumerator seekable = (ILogIndexEnumerator)enumerator;

        Assert.That(enumerator.MoveNext(), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((seekable.MoveTo(enumerator.Current), enumerator.Current), Is.EqualTo((true, _denseBlocks[0])));
            Assert.That((seekable.MoveTo(enumerator.Current - 1), enumerator.Current), Is.EqualTo((true, _denseBlocks[0])));
        }
    }

    [Test]
    public void MoveTo_past_range_end_completes()
    {
        using IEnumerator<int> enumerator = _storage.GetEnumerator(DenseAddress, 0, 1_000);
        ILogIndexEnumerator seekable = (ILogIndexEnumerator)enumerator;

        Assert.That((enumerator.MoveNext(), seekable.MoveTo(1_001), enumerator.MoveNext()), Is.EqualTo((true, false, false)));
    }

    // The forward-merge key holds the last MaxReorgDepth blocks and does not encode its first block in the key
    [TestCase(5_850)]
    [TestCase(5_900)]
    [TestCase(5_950)]
    [TestCase(BlockCount - 1)]
    public void MoveTo_into_forward_merge_chunk(int target)
    {
        foreach ((Address address, int[] blocks) in new[] { (DenseAddress, _denseBlocks), (SparseAddress, _sparseBlocks), (EarlyAddress, _earlyBlocks) })
        {
            using IEnumerator<int> enumerator = _storage.GetEnumerator(address, 0, BlockCount - 1);
            ILogIndexEnumerator seekable = (ILogIndexEnumerator)enumerator;

            int index = Array.BinarySearch(blocks, target);
            if (index < 0) index = ~index;

            if (index < blocks.Length)
                Assert.That((enumerator.MoveNext(), seekable.MoveTo(target), enumerator.Current), Is.EqualTo((true, true, blocks[index])), address.ToString());
            else
                Assert.That((enumerator.MoveNext(), seekable.MoveTo(target)), Is.EqualTo((true, false)), address.ToString());
        }
    }

    [Test]
    public void Missing_key_has_no_blocks()
    {
        using IEnumerator<int> enumerator = _storage.GetEnumerator(MissingAddress, 0, BlockCount - 1);
        Assert.That(enumerator.MoveNext(), Is.False);
    }

    private static int NextGap(Random random) => random.Next(4) switch
    {
        0 => 0,
        1 => random.Next(1, 4),
        2 => random.Next(4, 64),
        _ => random.Next(64, 1_500)
    };

    private static BlockReceipts[] CreateBatch(int start, int count, HashSet<int> sparse, HashSet<int> dense, bool descending)
    {
        BlockReceipts[] batch = new BlockReceipts[count];
        for (int i = 0; i < count; i++)
        {
            int block = descending ? start + count - 1 - i : start + i;
            List<LogEntry> logs = [Build.A.LogEntry.WithAddress(TestItem.AddressD).TestObject];
            if (sparse.Contains(block)) logs.Add(Build.A.LogEntry.WithAddress(SparseAddress).TestObject);
            if (dense.Contains(block)) logs.Add(Build.A.LogEntry.WithAddress(DenseAddress).TestObject);
            if (block < EarlyEnd && dense.Contains(block)) logs.Add(Build.A.LogEntry.WithAddress(EarlyAddress).TestObject);

            batch[i] = new(block, [Build.A.Receipt.WithLogs([.. logs]).TestObject]);
        }

        return batch;
    }
}
