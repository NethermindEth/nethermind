// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
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
    [Test]
    public void Exports_pinned_source_as_identical_canonical_fixture_with_bounded_sort(
        [Values("anchor", "a5")] string name, [Values(1024, 65536)] int bufferBytes)
    {
        string directory = Directory.CreateTempSubdirectory("pbt-offline-").FullName;
        try
        {
            string fixtures = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Eip8347");
            using JsonDocument blocks = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtures, "blocks.json")));
            JsonElement metadata = default;
            foreach (JsonElement block in blocks.RootElement.EnumerateArray())
                if (block.GetProperty("name").GetString() == name) metadata = block;
            BlockHeader header = Build.A.BlockHeader.WithNumber(metadata.GetProperty("number").GetUInt64())
                .WithTimestamp(0).WithStateRoot(new Hash256(metadata.GetProperty("mptRoot").GetString()!)).TestObject;
            PbtImageAnchor anchor = new("1", header.Hash!, header, 48, 24576);
            byte[] expectedSnapshot = File.ReadAllBytes(Path.Combine(fixtures, "canonical", name, "snapshot.pbt"));
            byte[] expectedPreimages = File.ReadAllBytes(Path.Combine(fixtures, "canonical", name, "preimages.bin"));
            using MemoryStream inputSnapshot = new(expectedSnapshot);
            using MemoryStream inputPreimages = new(expectedPreimages);
            using SnapshotableMemColumnsDb<FlatDbColumns> database = new("offline");
            using MemDb codes = new();
            PreimageRocksdbPersistence persistence = new(database, LimboLogs.Instance, FlatLayout.PreimageFlat);
            using (PbtVerifiedImage image = PbtImageVerifier.Verify(inputSnapshot, inputPreimages, anchor, directory, LimboLogs.Instance))
            using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(header), WriteFlags.None))
                image.Replay((address, account, code) =>
                {
                    batch.SetAccount(address, account);
                    if (code.Length != 0) codes[account.CodeHash.Bytes] = code;
                }, (address, slot, value) => batch.SetStorage(address, slot, new UInt256(value.Bytes, true)));
            using IPersistence.IPersistenceReader reader = persistence.CreateReader();
            using MemoryStream snapshot = new(), preimages = new();
            PbtOfflineSource.WriteArtifacts(reader, codes, anchor, directory, snapshot, preimages, LimboLogs.Instance, bufferBytes);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(snapshot.ToArray(), Is.EqualTo(expectedSnapshot));
                Assert.That(preimages.ToArray(), Is.EqualTo(expectedPreimages));
                Assert.That(reader.CurrentState, Is.EqualTo(new FlatStateId(header)));
                Assert.That(Directory.GetFileSystemEntries(directory), Is.Empty);
            }
            snapshot.Position = preimages.Position = 0;
            using PbtVerifiedImage verified = PbtImageVerifier.Verify(snapshot, preimages, anchor, directory, LimboLogs.Instance);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public void Includes_maximum_address_excluded_by_flat_iterator_upper_bound()
    {
        string directory = Directory.CreateTempSubdirectory("pbt-offline-").FullName;
        try
        {
            Address address = new("0xffffffffffffffffffffffffffffffffffffffff");
            BlockHeader header = Build.A.BlockHeader.TestObject;
            PbtImageAnchor anchor = new("1", header.Hash!, header, ulong.MaxValue, 24576);
            using SnapshotableMemColumnsDb<FlatDbColumns> database = new("offline");
            using MemDb codes = new();
            PreimageRocksdbPersistence persistence = new(database, LimboLogs.Instance, FlatLayout.PreimageFlat);
            using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(header), WriteFlags.None))
                batch.SetAccount(address, new Account(1, 100));
            using IPersistence.IPersistenceReader reader = persistence.CreateReader();
            using MemoryStream snapshot = new(), preimages = new();
            TestLogger log = new();
            PbtOfflineSource.WriteArtifacts(reader, codes, anchor, directory, snapshot, preimages,
                new OneLoggerLogManager(new ILogger(log)), 1024);
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
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public void Rejects_wrong_source_or_cancellation_without_writing_outputs([Values(false, true)] bool cancel)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> database = new("offline");
        using MemDb codes = new();
        PreimageRocksdbPersistence persistence = new(database, LimboLogs.Instance, FlatLayout.PreimageFlat);
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        BlockHeader header = Build.A.BlockHeader.TestObject;
        PbtImageAnchor anchor = new("1", header.Hash!, header, ulong.MaxValue, 24576);
        using MemoryStream snapshot = new(), preimages = new();
        Assert.That(() => PbtOfflineSource.WriteArtifacts(reader, codes, anchor, ".", snapshot, preimages, LimboLogs.Instance,
            cancellationToken: new CancellationToken(cancel)), cancel ? Throws.TypeOf<OperationCanceledException>() : Throws.TypeOf<InvalidDataException>());
        Assert.That(snapshot.Length + preimages.Length, Is.Zero);
    }

    /// <summary>A fan-in below the run count forces intermediate merge rounds before the final merge.</summary>
    [Test]
    public void Spool_merges_runs_in_key_order_collapsing_duplicates([Values(2, 3, 128)] int maxFanIn)
    {
        string directory = Directory.CreateTempSubdirectory("pbt-spool-").FullName;
        try
        {
            // A buffer of a few records per run, so a few hundred records spill into many runs.
            using PbtSortedSpool spool = new(directory, 512, LimboLogs.Instance, CancellationToken.None) { MaxFanIn = maxFanIn };
            SortedDictionary<ValueHash256, byte[]> expected = [];
            for (int index = 0; index < 400; index++)
            {
                ValueHash256 key = ValueKeccak.Compute(BitConverter.GetBytes(index % 250));
                byte[] value = ValueKeccak.Compute(key.Bytes).Bytes.ToArray();
                expected[key] = value;
                // Every key past 250 repeats an earlier one with the same value, so it must collapse.
                spool.Add(key.Bytes, value);
            }

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
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public void Spool_rejects_one_key_carrying_two_values([Values(true, false)] bool sameRun)
    {
        string directory = Directory.CreateTempSubdirectory("pbt-spool-").FullName;
        try
        {
            // A 512-byte buffer holds both records; padding the first run apart puts them in separate runs.
            using PbtSortedSpool spool = new(directory, 512, LimboLogs.Instance, CancellationToken.None) { MaxFanIn = 2 };
            byte[] key = ValueKeccak.Compute("key"u8).Bytes.ToArray();
            spool.Add(key, [1]);
            if (!sameRun) for (int index = 0; index < 16; index++) spool.Add(ValueKeccak.Compute(BitConverter.GetBytes(index)).Bytes, [2]);
            spool.Add(key, [3]);
            Assert.That(() => { using PbtSortedSpool.Cursor cursor = spool.Read(); while (cursor.MoveNext()) { } },
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("Conflicting"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
