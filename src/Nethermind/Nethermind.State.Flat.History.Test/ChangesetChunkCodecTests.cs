// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class ChangesetChunkCodecTests
{
    [Test]
    public void EncodeChunked_UnderTheCap_YieldsExactlyOneChunk()
    {
        List<ChangesetAccountEntry> entries = [Entry(TestItem.AddressA, slotCount: 3)];

        List<byte[]> chunks = ChangesetChunkCodec.EncodeChunked(entries, maxChunkBytes: 1_000_000).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chunks, Has.Count.EqualTo(1));
            AssertRoundTrips(entries, chunks);
        }
    }

    [Test]
    public void EncodeChunked_EmptyEntries_YieldsExactlyOneEmptyChunk()
    {
        List<ChangesetAccountEntry> entries = [];

        List<byte[]> chunks = ChangesetChunkCodec.EncodeChunked(entries, maxChunkBytes: 1024).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chunks, Has.Count.EqualTo(1));
            Assert.That(ChangesetChunkCodec.Decode(chunks[0]), Is.Empty);
        }
    }

    // Many small accounts, a cap tight enough to force several chunks: each chunk must decode entirely on its
    // own (no partial record straddling a chunk boundary) and the concatenated entries must reproduce the input
    // exactly, in order.
    [Test]
    public void EncodeChunked_ManyAccounts_SplitsAtEntryBoundaries_AndEachChunkDecodesIndependently()
    {
        List<ChangesetAccountEntry> entries = [];
        for (int i = 0; i < 50; i++)
        {
            entries.Add(Entry(TestItemAt(i), slotCount: 2));
        }

        List<byte[]> chunks = ChangesetChunkCodec.EncodeChunked(entries, maxChunkBytes: 300).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chunks, Has.Count.GreaterThan(1), "a 300-byte cap must force more than one chunk for 50 entries");
            foreach (byte[] chunk in chunks)
            {
                Assert.That(chunk.Length, Is.LessThanOrEqualTo(300), "no individual entry here is large enough to need exceeding the cap");
            }
            AssertRoundTrips(entries, chunks);
        }
    }

    // The destruct-heavy shape HistoryWriter.HandleSelfDestructV3's DestructSlotEnumerationCap exists for: one
    // account with thousands of slots, whose encoded changeset alone is well over the sidecar's 1MB chunk cap.
    // Splitting at whole-entry boundaries only cannot break a single entry across chunks, so it must go out as
    // one oversized chunk rather than fail or silently truncate — and that chunk alone must still decode.
    [Test]
    public void EncodeChunked_DestructHeavyBlockOverOneMegabyte_RoundTripsExactly()
    {
        const int slotCount = 35_000; // ~35_000 * ~36 bytes/slot > 1MB
        List<ChangesetAccountEntry> entries = [Entry(TestItem.AddressA, slotCount)];

        List<byte[]> chunks = ChangesetChunkCodec.EncodeChunked(entries, ChangesetSidecarStore.MaxChunkPayloadBytes).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chunks, Has.Count.EqualTo(1), "a single entry that alone exceeds the cap still goes out as one (oversized) chunk, not split mid-record");
            Assert.That(chunks[0].Length, Is.GreaterThan(ChangesetSidecarStore.MaxChunkPayloadBytes));
            AssertRoundTrips(entries, chunks);
        }
    }

    // A block with several ordinary accounts plus one destruct-heavy account: the small accounts pack into
    // normal-sized chunks and the oversized one gets its own, and every chunk is still independently decodable -
    // exactly the mixed shape a real destruct-heavy block produces alongside its other, unrelated changes.
    [Test]
    public void EncodeChunked_MixOfOrdinaryAndOversizedEntries_RoundTripsExactly()
    {
        List<ChangesetAccountEntry> entries = [
            Entry(TestItem.AddressA, slotCount: 2),
            Entry(TestItem.AddressB, slotCount: 20_000),
            Entry(TestItem.AddressC, slotCount: 2),
        ];

        List<byte[]> chunks = ChangesetChunkCodec.EncodeChunked(entries, ChangesetSidecarStore.MaxChunkPayloadBytes).ToList();

        AssertRoundTrips(entries, chunks);
    }

    // An empty PreValue means the key did not exist before this change (its first-ever touch) - the same
    // tombstone convention HistoryStoreV3.RecordPreValue documents for the read-path pre-value rows. Round-trips
    // through Encode/Decode distinctly from a non-empty PreValue, not collapsed into "absent field".
    [Test]
    public void Encode_Decode_EmptyPreValue_RoundTripsAsCreatedHere()
    {
        ChangesetAccountEntry entry = new(
            TestItem.AddressA,
            AccountChanged: true,
            AccountValue: new byte[] { 0x01 },
            AccountPreValue: ReadOnlyMemory<byte>.Empty,
            StorageChanges: [new ChangesetSlotEntry(1, new byte[] { 0xAA }, PreValue: ReadOnlyMemory<byte>.Empty)]);

        byte[] encoded = ChangesetChunkCodec.Encode([entry]);
        List<ChangesetAccountEntry> decoded = ChangesetChunkCodec.Decode(encoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded[0].AccountPreValue.Length, Is.EqualTo(0));
            Assert.That(decoded[0].StorageChanges[0].PreValue.Length, Is.EqualTo(0));
        }
    }

    private static void AssertRoundTrips(List<ChangesetAccountEntry> expected, List<byte[]> chunks)
    {
        List<ChangesetAccountEntry> decoded = [];
        foreach (byte[] chunk in chunks)
        {
            decoded.AddRange(ChangesetChunkCodec.Decode(chunk));
        }

        Assert.That(decoded, Has.Count.EqualTo(expected.Count));
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.That(decoded[i].Address, Is.EqualTo(expected[i].Address));
            Assert.That(decoded[i].AccountChanged, Is.EqualTo(expected[i].AccountChanged));
            Assert.That(decoded[i].AccountValue.ToArray(), Is.EqualTo(expected[i].AccountValue.ToArray()));
            Assert.That(decoded[i].AccountPreValue.ToArray(), Is.EqualTo(expected[i].AccountPreValue.ToArray()));
            Assert.That(decoded[i].StorageChanges, Has.Count.EqualTo(expected[i].StorageChanges.Count));
            for (int j = 0; j < expected[i].StorageChanges.Count; j++)
            {
                Assert.That(decoded[i].StorageChanges[j].Slot, Is.EqualTo(expected[i].StorageChanges[j].Slot));
                Assert.That(decoded[i].StorageChanges[j].Value.ToArray(), Is.EqualTo(expected[i].StorageChanges[j].Value.ToArray()));
                Assert.That(decoded[i].StorageChanges[j].PreValue.ToArray(), Is.EqualTo(expected[i].StorageChanges[j].PreValue.ToArray()));
            }
        }
    }

    private static ChangesetAccountEntry Entry(Address address, int slotCount)
    {
        List<ChangesetSlotEntry> slots = new(slotCount);
        for (int i = 0; i < slotCount; i++)
        {
            slots.Add(new ChangesetSlotEntry((UInt256)(i + 1), new byte[] { (byte)(i % 256), 0xAB }, new byte[] { (byte)(i % 256), 0xCD }));
        }

        return new ChangesetAccountEntry(address, AccountChanged: true, new byte[] { 0x01, 0x02, 0x03 }, new byte[] { 0x04, 0x05 }, slots);
    }

    private static Address TestItemAt(int i)
    {
        Span<byte> bytes = stackalloc byte[20];
        bytes[0] = 0xAA;
        bytes[18] = (byte)(i >> 8);
        bytes[19] = (byte)i;
        return new Address(bytes);
    }
}
