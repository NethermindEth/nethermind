// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;
using FlatStateId = Nethermind.State.Flat.StateId;
using Nethermind.State.Pbt.Image;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtOfflineSourceTests
{
    private string _directory = null!;
    private SnapshotableMemColumnsDb<FlatDbColumns> _database = null!;
    private MemDb _codes = null!;
    private PreimageRocksdbPersistence _persistence = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Directory.CreateTempSubdirectory("pbt-offline-").FullName;
        _database = new SnapshotableMemColumnsDb<FlatDbColumns>("offline");
        _codes = new MemDb();
        _persistence = new PreimageRocksdbPersistence(_database, LimboLogs.Instance, FlatLayout.PreimageFlat);
    }

    [TearDown]
    public void TearDown()
    {
        _database.Dispose();
        _codes.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public void Exports_pinned_source_as_identical_canonical_fixture_with_bounded_sort(
        [Values("anchor", "a5")] string name, [Values(1024, 65536)] int bufferBytes, [Values(1, 4)] int workerCount)
    {
        BlockHeader header = Eip8347FixtureState.AnchorHeader(name);
        PbtImageAnchor anchor = new("1", header.Hash!, header, 48);
        byte[] expectedSnapshot = File.ReadAllBytes(Eip8347FixtureState.ArtifactPath(name, "snapshot.pbt"));
        byte[] expectedPreimages = File.ReadAllBytes(Eip8347FixtureState.ArtifactPath(name, "preimages.bin"));
        using MemoryStream inputSnapshot = new(expectedSnapshot);
        using MemoryStream inputPreimages = new(expectedPreimages);
        Eip8347FixtureState.ReplayInto(_persistence, _codes, header, inputSnapshot, inputPreimages);
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using MemoryStream snapshot = new(), preimages = new();
        PbtOfflineSource.WriteArtifacts(reader, _codes, anchor, _directory, snapshot, preimages, LimboLogs.Instance,
            bufferBytes, workerCount, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.ToArray(), Is.EqualTo(expectedSnapshot));
            Assert.That(preimages.ToArray(), Is.EqualTo(expectedPreimages));
            Assert.That(reader.CurrentState, Is.EqualTo(new FlatStateId(header)));
            Assert.That(Directory.GetFileSystemEntries(_directory), Is.Empty);
        }
    }

    /// <remarks>Only the last address range can reach it, and only by an explicit lookup, so a partitioned scan
    /// must still emit it exactly once however many workers share the walk.</remarks>
    [Test]
    public void Includes_maximum_address_excluded_by_flat_iterator_upper_bound([Values(1, 4)] int workerCount)
    {
        Address address = new("0xffffffffffffffffffffffffffffffffffffffff");
        BlockHeader header = Build.A.BlockHeader.TestObject;
        PbtImageAnchor anchor = new("1", header.Hash!, header, ulong.MaxValue);
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(header), WriteFlags.None))
            batch.SetAccount(address, new Account(1, 100));
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using MemoryStream snapshot = new(), preimages = new();
        TestLogger log = new();
        PbtOfflineSource.WriteArtifacts(reader, _codes, anchor, _directory, snapshot, preimages,
            new OneLoggerLogManager(new ILogger(log)), 1024, workerCount, CancellationToken.None);
        preimages.Position = 0;
        PbtPreimageReader output = new(preimages);
        Assert.That(output.ReadAccount(out Address? actual, out uint slots), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(address));
            Assert.That(slots, Is.Zero);
            Assert.That(output.ReadAccount(out _, out _), Is.False);
            Assert.That(log.LogList, Has.Some.Contains("for 1 accounts and 0 slots"));
        }
    }

    [Test]
    public void Rejects_wrong_source_or_cancellation_without_writing_outputs([Values(false, true)] bool cancel)
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        BlockHeader header = Build.A.BlockHeader.TestObject;
        PbtImageAnchor anchor = new("1", header.Hash!, header, ulong.MaxValue);
        using MemoryStream snapshot = new(), preimages = new();
        Assert.That(() => PbtOfflineSource.WriteArtifacts(reader, _codes, anchor, _directory, snapshot, preimages, LimboLogs.Instance,
            sortBufferBytes: 1024, workerCount: 0, new CancellationToken(cancel)),
            cancel ? Throws.TypeOf<OperationCanceledException>() : Throws.TypeOf<InvalidDataException>());
        Assert.That(snapshot.Length + preimages.Length, Is.Zero);
    }

    /// <summary>A fan-in or pre-merge threshold below the run count forces intermediate merge rounds.</summary>
    /// <remarks>The writer count exercises the concurrent path: every key must still surface exactly once,
    /// however the partitioned producers happened to spread it across runs.</remarks>
    [Test]
    public void Spool_merges_runs_in_key_order_collapsing_duplicates(
        [Values(2, 3, 128)] int maxFanIn, [Values(2, 64)] int preMergeThreshold, [Values(1, 4)] int writerCount,
        [Values(1, 2)] int maxConcurrentPreMerges)
    {
        // A buffer of a few records per run, so a few hundred records spill into many runs.
        using PbtSortedSpool spool = new("test", _directory, 512, writerCount, LimboLogs.Instance, CancellationToken.None)
        {
            MaxFanIn = maxFanIn,
            PreMergeThreshold = preMergeThreshold,
            MaxConcurrentPreMerges = maxConcurrentPreMerges
        };
        SortedDictionary<ValueHash256, byte[]> expected = [];
        for (int index = 0; index < 400; index++)
        {
            ValueHash256 key = ValueKeccak.Compute(BitConverter.GetBytes(index % 250));
            expected[key] = ValueKeccak.Compute(key.Bytes).Bytes.ToArray();
        }

        // Every key past 250 repeats an earlier one with the same value, so it must collapse.
        Parallel.For(0, writerCount, new ParallelOptions { MaxDegreeOfParallelism = writerCount }, worker =>
        {
            using PbtSortedSpool.Writer writer = spool.CreateWriter();
            for (int index = worker; index < 400; index += writerCount)
            {
                ValueHash256 key = ValueKeccak.Compute(BitConverter.GetBytes(index % 250));
                writer.Add(key.Bytes, ValueKeccak.Compute(key.Bytes).Bytes);
            }
        });

        // Read twice: the runs outlive the merge, so a second cursor must replay the same sequence.
        for (int pass = 0; pass < 2; pass++)
        {
            using PbtSortedSpool.Cursor cursor = spool.Read();
            using IEnumerator<KeyValuePair<ValueHash256, byte[]>> reference = expected.GetEnumerator();
            while (cursor.MoveNext())
            {
                Assert.That(reference.MoveNext(), Is.True, "more merged records than distinct keys");
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(cursor.Key.ToArray(), Is.EqualTo(reference.Current.Key.Bytes.ToArray()), "key");
                    Assert.That(cursor.Value.ToArray(), Is.EqualTo(reference.Current.Value), "value");
                }
            }
            Assert.That(reference.MoveNext(), Is.False, "fewer merged records than distinct keys");
        }
    }
}
