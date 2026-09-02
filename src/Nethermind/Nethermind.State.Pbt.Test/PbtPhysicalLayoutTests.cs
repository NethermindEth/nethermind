// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtPhysicalLayoutTests
{
    [Test]
    public void Physical_layouts_retain_the_same_canonical_records_and_root_across_batches()
    {
        PbtPhysicalNodeStore record = new(PbtNodeLayout.Record);
        PbtPhysicalNodeStore grouped = new(PbtNodeLayout.HashBucket);
        EipReferenceTree oracle = new();
        (byte[] Key, byte Marker)[] initial =
        [
            ([0x12, 0x00], 1),
            ([0x12, 0x80], 2),
            ([0x10, 0x00], 3),
            ([0x12, 0x40], 4),
        ];

        PbtWriteBatch first = Batch(initial);
        ValueHash256 recordRoot = TrieUpdater.UpdateRoot(record, default, first);
        ValueHash256 groupedRoot = TrieUpdater.UpdateRoot(grouped, default, first);
        foreach ((byte[] key, byte marker) in initial) oracle.Insert(key, Value(marker));
        AssertCanonical(recordRoot, groupedRoot, oracle, record, grouped);

        PbtPhysicalNodeStore reconstructedRecord = PbtPhysicalNodeStore.FromPhysicalPayloads(
            PbtNodeLayout.Record, recordRoot, record.ExportPhysicalPayloads());
        PbtPhysicalNodeStore reconstructedGrouped = PbtPhysicalNodeStore.FromPhysicalPayloads(
            PbtNodeLayout.HashBucket, groupedRoot, grouped.ExportPhysicalPayloads());
        AssertReadable(record, reconstructedRecord);
        AssertReadable(grouped, reconstructedGrouped);
        AssertUntouchedBucketsRemainExact(reconstructedGrouped);

        record.Layout = PbtNodeLayout.HashBucket;
        grouped.Layout = PbtNodeLayout.Record;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(record.ExportPhysicalPayloads(), Is.Not.Empty, "changing layout immediately rebuilds physical groups");
            Assert.That(grouped.ExportPhysicalPayloads(), Has.Count.EqualTo(grouped.EnumerateRecords().Count));
        }
        PbtWriteBatch second = new();
        second.Delete(new PbtFullKey([0x12, 0x80]));
        second.Set(new PbtFullKey([0x12, 0x20]), new ValueHash256(Value(5)));
        second.Set(new PbtFullKey([0x12, 0x00]), new ValueHash256(Value(6)));
        recordRoot = TrieUpdater.UpdateRoot(record, recordRoot, second);
        groupedRoot = TrieUpdater.UpdateRoot(grouped, groupedRoot, second);
        oracle.Delete([0x12, 0x80]);
        oracle.Insert([0x12, 0x20], Value(5));
        oracle.Insert([0x12, 0x00], Value(6));
        AssertCanonical(recordRoot, groupedRoot, oracle, record, grouped);

        recordRoot = TrieUpdater.UpdateRoot(reconstructedRecord, reconstructedRecord.RootHash, second);
        groupedRoot = TrieUpdater.UpdateRoot(reconstructedGrouped, reconstructedGrouped.RootHash, second);
        AssertCanonical(recordRoot, groupedRoot, oracle, reconstructedRecord, reconstructedGrouped);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(record.ExportPhysicalPayloads(), Is.Not.Empty);
            Assert.That(grouped.ExportPhysicalPayloads(), Is.Not.Empty);
        }
    }

    private static void AssertReadable(PbtPhysicalNodeStore expected, PbtPhysicalNodeStore reconstructed)
    {
        foreach (PbtNodeRecord record in expected.EnumerateRecords())
        {
            Assert.That(reconstructed.GetNode(record.Path), Is.EqualTo(record.Encoding.ToArray()));
        }
        Assert.That(CanonicalSet(reconstructed), Is.EqualTo(CanonicalSet(expected)));
    }

    private static void AssertUntouchedBucketsRemainExact(PbtPhysicalNodeStore store)
    {
        Dictionary<byte, byte[]> before = PhysicalPayloads(store);
        PbtNodeRecord changedRecord = store.EnumerateRecords()[0];
        byte changedBucket = Blake3Hash.Hash(changedRecord.Path.Encode()).Bytes[0];
        byte[] encoding = changedRecord.Encoding.ToArray();
        store.Apply(store.RootHash, [], [new PbtNodeMutation(changedRecord.Path, encoding)]);
        Dictionary<byte, byte[]> after = PhysicalPayloads(store);
        foreach ((byte key, byte[] payload) in before)
        {
            if (key != changedBucket) Assert.That(after[key], Is.EqualTo(payload), $"untouched bucket {key:x2}");
        }
    }

    private static Dictionary<byte, byte[]> PhysicalPayloads(PbtPhysicalNodeStore store)
    {
        Dictionary<byte, byte[]> result = [];
        foreach (PbtPhysicalPayload payload in store.ExportPhysicalPayloads()) result.Add(payload.Key.Span[0], payload.Payload.ToArray());
        return result;
    }

    private static PbtWriteBatch Batch(IEnumerable<(byte[] Key, byte Marker)> entries)
    {
        PbtWriteBatch batch = new();
        foreach ((byte[] key, byte marker) in entries) batch.Set(new PbtFullKey(key), new ValueHash256(Value(marker)));
        return batch;
    }

    private static void AssertCanonical(
        in ValueHash256 recordRoot,
        in ValueHash256 groupedRoot,
        EipReferenceTree oracle,
        PbtPhysicalNodeStore record,
        PbtPhysicalNodeStore grouped)
    {
        string[] recordSet = CanonicalSet(record);
        string[] groupedSet = CanonicalSet(grouped);
        bool hasCrossBoundaryPath = false;
        foreach (PbtNodeRecord node in record.EnumerateRecords())
        {
            if (node.Path.BitDepth > 8 && (node.Path.BitDepth & 7) != 0) hasCrossBoundaryPath = true;
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recordRoot.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(groupedRoot, Is.EqualTo(recordRoot));
            Assert.That(groupedSet, Is.EqualTo(recordSet));
            Assert.That(hasCrossBoundaryPath, Is.True, "a non-byte-aligned path crosses physical grouping boundaries");
        }
    }

    private static string[] CanonicalSet(PbtPhysicalNodeStore store)
    {
        IReadOnlyList<PbtNodeRecord> records = store.EnumerateRecords();
        string[] result = new string[records.Count];
        for (int index = 0; index < result.Length; index++)
        {
            PbtNodeRecord record = records[index];
            result[index] = Convert.ToHexString(record.Path.Encode()) + Convert.ToHexString(record.Encoding.Span);
        }
        return result;
    }

    private static byte[] Value(byte marker)
    {
        byte[] value = new byte[ValueHash256.MemorySize];
        value[^1] = marker;
        return value;
    }
}
