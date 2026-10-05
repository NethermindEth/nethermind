// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.IO;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Test.Api;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Snappier;

namespace Nethermind.BeaconChain.Test.Storage;

public class BeaconChainStoreBlockSlotTests
{
    private const ulong Slot = 412_500 * 32 + 3;
    private static readonly Hash256 Root = BeaconApiTestHost.TestRoot(0x40);

    internal static readonly byte[] NotSnappy = [0xff, 0xff, 0xff, 0xff, 0xff, 0xff];

    internal static readonly byte[] ClaimsFourGiB = [0xff, 0xff, 0xff, 0xff, 0x0f, 0x00];

    internal static readonly byte[] ClaimsTwoGiB = [0xf0, 0xff, 0xff, 0xff, 0x07, 0x00];

    private MemColumnsDb<BeaconChainDbColumns> _db = null!;
    private BeaconChainStore _store = null!;

    [SetUp]
    public void CreateStore()
    {
        _db = new MemColumnsDb<BeaconChainDbColumns>();
        _store = new BeaconChainStore(_db, BeaconChainSpec.Mainnet);
    }

    [TearDown]
    public void DisposeStore() => _db.Dispose();

    [Test]
    public void Reads_the_slot_of_a_block_stored_through_the_store()
    {
        _store.PutBlock(Root, BeaconApiTestHost.RichBlock(Slot, BeaconApiTestHost.FilledHash(0x01)));

        Assert.That(_store.TryGetBlockSlot(Root, out ulong slot), Is.True);
        Assert.That(slot, Is.EqualTo(Slot));
    }

    [Test]
    public void Reads_the_slot_of_a_record_whose_body_would_not_decode()
    {
        WriteRecord(BeaconApiTestHost.SlotPrefixOnlyBlockSsz(Slot));

        Assert.That(_store.TryGetBlockSlot(Root, out ulong slot), Is.True);
        Assert.That(slot, Is.EqualTo(Slot));
        Assert.That(() => _store.TryGetForkedBlock(Root, out _), Throws.Exception, "the record really is undecodable, so reading its slot proves no decode ran");
    }

    [Test]
    public void Is_false_for_a_root_with_no_stored_block()
    {
        Assert.That(_store.TryGetBlockSlot(Root, out ulong slot), Is.False);
        Assert.That(slot, Is.Zero);
    }

    [TestCase(107, 100u, TestName = "One byte short of the slot")]
    [TestCase(108, 99u, TestName = "Message offset is not the fixed-part length")]
    public void Throws_for_a_record_that_is_not_a_signed_beacon_block_prefix(int length, uint messageOffset)
    {
        byte[] ssz = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(ssz, messageOffset);
        WriteRecord(ssz);

        Assert.That(() => _store.TryGetBlockSlot(Root, out _), Throws.TypeOf<BeaconStateException>());
    }

    // A literal longer than the claimed length must not be read past it, or a 50-byte record would yield a slot.
    [Test]
    public void Throws_for_a_record_that_claims_less_than_the_slot_prefix_it_carries()
    {
        byte[] record = [50, 60 << 2, 107, .. BeaconApiTestHost.SlotPrefixOnlyBlockSsz(Slot)];
        _db.GetColumnDb(BeaconChainDbColumns.Blocks).Set(Root.Bytes, record);

        Assert.That(() => _store.TryGetBlockSlot(Root, out _), Throws.TypeOf<BeaconStateException>());
    }

    [Test]
    public void Throws_for_a_record_that_is_not_snappy()
    {
        _db.GetColumnDb(BeaconChainDbColumns.Blocks).Set(Root.Bytes, NotSnappy);

        Assert.That(() => _store.TryGetBlockSlot(Root, out _), Throws.TypeOf<InvalidDataException>());
    }

    // Every block the network carries is at most MAX_PAYLOAD_SIZE, so a larger claim is corruption, refused before any allocation of it.
    [TestCase(ReqRespFraming.MaxPayloadSize, true, TestName = "A claim of exactly MAX_PAYLOAD_SIZE is read")]
    [TestCase(ReqRespFraming.MaxPayloadSize + 1, false, TestName = "A claim above MAX_PAYLOAD_SIZE is refused")]
    public void Bounds_the_claimed_length_and_decompresses_only_the_slot_prefix(int claimed, bool read)
    {
        byte[] ssz = new byte[claimed];
        BeaconApiTestHost.SlotPrefixOnlyBlockSsz(Slot).CopyTo(ssz, 0);
        WriteRecord(ssz);

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool found = false;
        ulong slot = 0;
        InvalidDataException? error = null;
        try
        {
            found = _store.TryGetBlockSlot(Root, out slot);
        }
        catch (InvalidDataException e)
        {
            error = e;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(error is null, Is.EqualTo(read), error?.Message);
        Assert.That(found && slot == Slot, Is.EqualTo(read));
        Assert.That(allocated, Is.LessThan(claimed / 4), "the slot read must not decompress the whole record");
    }

    [TestCaseSource(nameof(OverLargeClaims))]
    public void Refuses_a_claimed_length_that_a_full_decompression_would_fail_on(byte[] record)
    {
        _db.GetColumnDb(BeaconChainDbColumns.Blocks).Set(Root.Bytes, record);

        Assert.That(() => _store.TryGetBlockSlot(Root, out _), Throws.TypeOf<InvalidDataException>().With.Message.Contains("claims"));
    }

    private static IEnumerable<TestCaseData> OverLargeClaims()
    {
        yield return new TestCaseData(ClaimsFourGiB).SetArgDisplayNames("claims 2^32 - 1 bytes");
        yield return new TestCaseData(ClaimsTwoGiB).SetArgDisplayNames("claims 0x7FFFFFF0 bytes");
    }

    // Each record decompresses to the 108-byte prefix of a block.
    [TestCaseSource(nameof(HandBuiltPrefixes))]
    public void Reads_the_slot_through_every_snappy_element_type(byte[] record, ulong expectedSlot)
    {
        _db.GetColumnDb(BeaconChainDbColumns.Blocks).Set(Root.Bytes, record);

        Assert.That(_store.TryGetBlockSlot(Root, out ulong slot), Is.True);
        Assert.That(slot, Is.EqualTo(expectedSlot));
    }

    private static IEnumerable<TestCaseData> HandBuiltPrefixes()
    {
        byte[] messageOffset = [0x64, 0x00, 0x00, 0x00];
        byte[] fill = Enumerable.Repeat((byte)0xab, 104).ToArray();
        byte[] head = Concat([108, 4 << 2], messageOffset, [0xab]);
        const ulong filled = 0xABABABABABABABABUL;
        yield return new TestCaseData(Concat([108, 60 << 2, 107], messageOffset, fill), filled).SetArgDisplayNames("literal with a one-byte length");
        yield return new TestCaseData(Concat([108, 61 << 2, 107, 0], messageOffset, fill), filled).SetArgDisplayNames("literal with a two-byte length");
        yield return new TestCaseData(Concat(head, Enumerable.Repeat(new byte[] { (7 << 2) | 1, 1 }, 9).SelectMany(copy => copy), [(0 << 2) | 1, 1]), filled).SetArgDisplayNames("overlapping one-byte-offset copies");
        yield return new TestCaseData(Concat(head, [(63 << 2) | 2, 1, 0, (38 << 2) | 2, 1, 0]), filled).SetArgDisplayNames("two-byte-offset copies");
        yield return new TestCaseData(Concat(head, [(63 << 2) | 3, 1, 0, 0, 0, (38 << 2) | 3, 1, 0, 0, 0]), filled).SetArgDisplayNames("four-byte-offset copies");
        // An offset equal to the bytes written so far copies from the very first byte, which the format allows.
        yield return new TestCaseData(Concat([108, 60 << 2, 99], messageOffset, fill.Take(96), [(7 << 2) | 2, 100, 0]), 0xABABABAB00000064UL).SetArgDisplayNames("copy from the first byte");
    }

    [TestCaseSource(nameof(MalformedPrefixes))]
    public void Throws_for_a_malformed_snappy_prefix(byte[] record)
    {
        _db.GetColumnDb(BeaconChainDbColumns.Blocks).Set(Root.Bytes, record);

        Assert.That(() => _store.TryGetBlockSlot(Root, out _), Throws.TypeOf<InvalidDataException>());
    }

    private static IEnumerable<TestCaseData> MalformedPrefixes()
    {
        yield return new TestCaseData(new byte[] { 0x80 }).SetArgDisplayNames("length varint truncated");
        // Snappy lengths are 32-bit varints, so a sixth length byte is malformed even when the value it spells is small.
        yield return new TestCaseData(Concat([0xec, 0x80, 0x80, 0x80, 0x80, 0x00, 60 << 2, 107, 0x64, 0x00, 0x00, 0x00], Enumerable.Repeat((byte)0xab, 104))).SetArgDisplayNames("length varint of six bytes");
        yield return new TestCaseData(new byte[] { 108 }).SetArgDisplayNames("no element after the length");
        yield return new TestCaseData(new byte[] { 108, 60 << 2 }).SetArgDisplayNames("literal length byte missing");
        yield return new TestCaseData(new byte[] { 108, 10 << 2, 0x64, 0 }).SetArgDisplayNames("literal shorter than its length");
        yield return new TestCaseData(new byte[] { 108, (7 << 2) | 1, 1 }).SetArgDisplayNames("copy before any byte is written");
        yield return new TestCaseData(new byte[] { 108, 0, 0x64, (7 << 2) | 1, 2 }).SetArgDisplayNames("copy offset beyond the written bytes");
        // The prefix is otherwise complete, so the zero offset is the only defect.
        yield return new TestCaseData(Concat([108, 60 << 2, 99, 0x64, 0x00, 0x00, 0x00], Enumerable.Repeat((byte)0xab, 96), [(4 << 2) | 1, 0])).SetArgDisplayNames("copy offset zero");
        yield return new TestCaseData(Concat([108, 4 << 2, 0x64, 0x00, 0x00, 0x00, 0xab], [(1 << 5) | (7 << 2) | 1, 1, (63 << 2) | 2, 1, 0, (27 << 2) | 2, 1, 0])).SetArgDisplayNames("one-byte-offset copy whose high offset bits reach past the written bytes");
        yield return new TestCaseData(new byte[] { 108, 0, 0x64, (7 << 2) | 3, 1, 0 }).SetArgDisplayNames("copy offset truncated");
    }

    // The prefix read must agree with Snappier on every record Snappier writes, whichever elements it chooses.
    [Test]
    public void Reads_the_slot_Snappier_wrote_for_any_block_prefix([Range(0, 63)] int seed)
    {
        Random random = new(seed);
        byte[] ssz = new byte[108 + random.Next(0, 4096)];
        int period = 1 + random.Next(0, 16);
        for (int i = 0; i < ssz.Length; i++) ssz[i] = seed % 4 == 0 ? (byte)random.Next() : (byte)(i % period * 37);
        BinaryPrimitives.WriteUInt32LittleEndian(ssz, 100);
        if (seed % 2 == 0) BinaryPrimitives.WriteUInt64LittleEndian(ssz.AsSpan(100), (ulong)random.NextInt64());
        WriteRecord(ssz);

        Assert.That(_store.TryGetBlockSlot(Root, out ulong slot), Is.True);
        Assert.That(slot, Is.EqualTo(BinaryPrimitives.ReadUInt64LittleEndian(ssz.AsSpan(100))));
    }

    private static byte[] Concat(params IEnumerable<byte>[] parts) => parts.SelectMany(part => part).ToArray();

    private void WriteRecord(ReadOnlySpan<byte> ssz) =>
        _db.GetColumnDb(BeaconChainDbColumns.Blocks).Set(Root.Bytes, Snappy.CompressToArray(ssz));
}
