// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Utils;
using Nethermind.State.Flat.Hsst.BSearchIndex;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;
using Nethermind.State.Flat.Hsst.BTree;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Unit tests for BSearchIndexReader (B-tree navigation) and BSearchIndexWriter (B-tree construction).
/// Hex fixture tests document the exact binary format of each node type.
/// </summary>
[TestFixture]
public class BSearchIndexTests
{
    // Read the root node from a full-HSST byte array.
    // Trailer is [RootPrefix bytes][RootPrefixLen u8][RootSize u16 LE][KeyLength u8][IndexType u8].
    private static BSearchIndexReader ReadHsstRoot(byte[] data)
    {
        int rootPrefixLen = data[data.Length - 5];
        int rootSize = data[data.Length - 4] | (data[data.Length - 3] << 8);
        int rootStart = data.Length - 5 - rootPrefixLen - rootSize;
        ReadOnlySpan<byte> rootPrefix = rootPrefixLen > 0
            ? data.AsSpan(data.Length - 5 - rootPrefixLen, rootPrefixLen)
            : default;
        return BSearchIndexReader.ReadFromStart(data, rootStart, rootPrefix);
    }

    // ===== METADATA READING TESTS =====

    [Test]
    public void IndexMetadata_ReadFromEnd_MinimalNode()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) => { });

        BSearchIndexReader index = ReadHsstRoot(data);
        Assert.That(index.EntryCount, Is.EqualTo(0));
        Assert.That(index.Metadata.KeyCount, Is.EqualTo(0));
    }

    [Test]
    public void IndexMetadata_WithBaseOffset_ParsedCorrectly()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
        {
            for (int i = 0; i < 10; i++)
            {
                byte[] key = new byte[4];
                key[3] = (byte)i;
                builder.Add(key, new byte[] { (byte)i });
            }
        });

        BSearchIndexReader rootIndex = ReadHsstRoot(data);
        Assert.That(rootIndex.EntryCount, Is.EqualTo(10));
    }

    [Test]
    public void BSearchIndex_EmptyIndex_HandlesCorrectly()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) => { });

        BSearchIndexReader index = ReadHsstRoot(data);
        Assert.That(index.EntryCount, Is.EqualTo(0));
        Assert.That(index.TryGetFloor("abc"u8, out _, out _), Is.False);
    }

    [Test]
    public void BSearchIndex_SingleLeafNode_StructureValid()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
        {
            builder.Add([0x41, 0x42], [0x01, 0x02, 0x03]);
        });

        BSearchIndexReader rootIndex = ReadHsstRoot(data);
        Assert.That(rootIndex.EntryCount, Is.EqualTo(1));
    }

    // ===== HEX FIXTURE TESTS: UNIFORM KEYS =====

    private static IEnumerable<TestCaseData> UniformKeysTestCases()
    {
        // Single entry: separator=0x41 ('A'), value=100, keyLen=1
        // Header sits at the front; keys section then values section follow.
        //
        // Expected binary layout (header fields are fixed-width LE; no LEB128):
        //   "25"           - Flags: NodeKind=Intermediate(01)|KeyType=Uniform(01<<2=04)|ValueSizeCode=10→4 bytes (10<<4=0x20)
        //   "0100"         - KeyCount: 1 (u16 LE)
        //   "0100"         - KeySize: 1 (u16 LE — fixed key length)
        //   "00"           - CommonPrefixLen: 0 (mandatory u8; 0 = no prefix)
        //   "000000000000" - BaseOffset: 0 (mandatory 6-byte LE — sits at end of header)
        //   "41"           - Keys[0]: separator byte 0x41 (Uniform, 1 byte)
        //   "64000000"     - Values[0]: 100 as int32 LE (ValueSize=4 from flags code)
        yield return new TestCaseData(
            new[] { "41" }, new[] { 100 }, 1,
            "25" + "0100" + "0100" + "00" + "000000000000" + "41" + "64000000"
        ).SetName("Uniform_SingleEntry");

        // Three entries: separators=[0x41,0x43,0x45], values=[0,100,200], keyLen=1
        // BaseOffset = 0 here (writer didn't strip it; test exercises the BSearchIndexWriter
        // with an explicit ValueSlotSize=4, so values stay 4-byte int32 LE).
        //
        //   "25"           - Flags (NodeKind=Intermediate|KeyType=Uniform|ValueSizeCode=10→4 bytes)
        //   "0300"         - KeyCount: 3
        //   "0100"         - KeySize: 1
        //   "00"           - CommonPrefixLen: 0
        //   "000000000000" - BaseOffset: 0
        //   "41 43 45"     - Keys[0..2]
        //   "00000000"     - Values[0]: 0 as int32 LE
        //   "64000000"     - Values[1]: 100 as int32 LE
        //   "C8000000"     - Values[2]: 200 as int32 LE
        yield return new TestCaseData(
            new[] { "41", "43", "45" }, new[] { 0, 100, 200 }, 1,
            "25" + "0300" + "0100" + "00" + "000000000000" + "41" + "43" + "45" + "00000000" + "64000000" + "C8000000"
        ).SetName("Uniform_ThreeEntries");
    }

    [TestCaseSource(nameof(UniformKeysTestCases))]
    public void IndexBuilder_UniformKeys_ProducesCorrectBinary(string[] separatorHexes, int[] values, int keyLen, string expectedHex)
    {
        byte[] output = new byte[1024];
        int keyBufSize = 0;
        for (int i = 0; i < separatorHexes.Length; i++) keyBufSize += 2 + separatorHexes[i].Length / 2;
        Span<byte> keyBuf = stackalloc byte[keyBufSize];
        SpanBufferWriter bufWriter = new(output);
        Span<byte> valScratch = stackalloc byte[separatorHexes.Length * (2 + 4)];
        BSearchIndexWriter<SpanBufferWriter> writer = new(ref bufWriter, new BSearchIndexMetadata { KeyType = 1, KeySlotSize = keyLen }, keyBuf, valScratch);
        Span<byte> valBuf = stackalloc byte[4];
        for (int i = 0; i < separatorHexes.Length; i++)
        {
            byte[] key = separatorHexes[i].Length > 0 ? Convert.FromHexString(separatorHexes[i]) : [];
            BinaryPrimitives.WriteInt32LittleEndian(valBuf, values[i]);
            writer.AddKey(key, valBuf);
        }
        writer.FinalizeNode();
        int written = (int)bufWriter.Written;

        Assert.That(Convert.ToHexString(output[..written]), Is.EqualTo(expectedHex));

        // Also verify the reader parses the binary correctly
        BSearchIndexReader index = BSearchIndexReader.ReadFromStart(output, 0);
        Assert.That(index.EntryCount, Is.EqualTo(separatorHexes.Length));
        Span<byte> keyBufRead = stackalloc byte[64];
        for (int i = 0; i < separatorHexes.Length; i++)
        {
            byte[] expectedSep = separatorHexes[i].Length > 0 ? Convert.FromHexString(separatorHexes[i]) : [];
            int len = index.GetFullKey(i, keyBufRead);
            Assert.That(keyBufRead[..len].ToArray(), Is.EqualTo(expectedSep), $"Entry {i} separator mismatch");
            Assert.That(index.GetUInt64Value(i), Is.EqualTo((ulong)values[i]), $"Entry {i} value mismatch");
        }
    }

    [Test]
    public void IndexBuilder_UniformKeys_WithBaseOffset()
    {
        // Three entries with values=[100,200,300]. Caller pre-subtracts baseOffset=100.
        // BaseOffset is mandatory (6 bytes LE).
        //
        //   "25"           - Flags: NodeKind=Intermediate|KeyType=Uniform|ValueSizeCode=10→4 bytes
        //   "0300"         - KeyCount: 3
        //   "0100"         - KeySize: 1
        //   "00"           - CommonPrefixLen: 0
        //   "640000000000" - BaseOffset: 100 (mandatory 6-byte LE — sits at end of header)
        //   "41 43 45"     - Keys[0..2]
        //   "00000000"     - Values[0]: 100-100=0 as int32 LE
        //   "64000000"     - Values[1]: 200-100=100 as int32 LE
        //   "C8000000"     - Values[2]: 300-100=200 as int32 LE
        string expectedHex = "25" + "0300" + "0100" + "00" + "640000000000" + "41" + "43" + "45" + "00000000" + "64000000" + "C8000000";

        ulong baseOffset = 100;
        byte[] output = new byte[1024];
        Span<byte> keyBuf = stackalloc byte[3 * (2 + 1)]; // 3 entries, each key is 1 byte
        Span<byte> valScratch = stackalloc byte[3 * (2 + 4)];
        SpanBufferWriter bufWriter = new(output);
        BSearchIndexWriter<SpanBufferWriter> writer = new(ref bufWriter, new BSearchIndexMetadata { KeyType = 1, KeySlotSize = 1, BaseOffset = baseOffset }, keyBuf, valScratch);
        Span<byte> valBuf = stackalloc byte[4];
        foreach ((string sepHex, int val) in new[] { ("41", 100), ("43", 200), ("45", 300) })
        {
            BinaryPrimitives.WriteInt32LittleEndian(valBuf, val - (int)baseOffset);
            writer.AddKey(Convert.FromHexString(sepHex), valBuf);
        }
        writer.FinalizeNode();
        int written = (int)bufWriter.Written;

        Assert.That(Convert.ToHexString(output[..written]), Is.EqualTo(expectedHex));

        BSearchIndexReader index = BSearchIndexReader.ReadFromStart(output, 0);
        Assert.That(index.Metadata.BaseOffset, Is.EqualTo((ulong)100));
        Assert.That(index.GetUInt64Value(0), Is.EqualTo((ulong)100));
        Assert.That(index.GetUInt64Value(1), Is.EqualTo((ulong)200));
        Assert.That(index.GetUInt64Value(2), Is.EqualTo((ulong)300));
    }

    // ===== HEX FIXTURE TESTS: VARIABLE KEYS =====

    private static IEnumerable<TestCaseData> VariableKeysTestCases()
    {
        // Two entries: empty separator + "7A8B49" (3 bytes).
        // Empty first entry forces Variable key format. Variable always sets the LE key flag
        // (bit 6) since prefixArr is uniformly 2 bytes/slot. No BaseOffset.
        //
        //   "61"       - Flags: NodeKind=Intermediate(01)|KeyType=Variable(00<<2)|ValueSizeCode=10→4 bytes (10<<4=0x20)|LEKey(1<<6=0x40)
        //   "0200"     - KeyCount: 2
        //   "0900"     - KeySize: 9 (2*2 prefixArr + 2*2 offsetArr + 1 remainingkeys)
        //   "00"       - CommonPrefixLen: 0
        //   "000000000000" - BaseOffset: 0 (6-byte LE — sits at end of header)
        //   "0000"     - prefixArr[0]: empty key → padded zeros (LE-stored)
        //   "8B7A"     - prefixArr[1]: byte-reversed first 2 bytes of "7A8B49" = [8B, 7A]
        //   "0000"     - offsetArr[0]: tag=00, tailOffset=0 (no tail)
        //   "00C0"     - offsetArr[1]: tag=11, tailOffset=0; raw u16=0xC000 → LE [00, C0]
        //   "49"       - remainingkeys: tail of entry 1 ("49"; first 2 bytes are in prefixArr)
        //   "00000000" - Values[0]: 0 as int32 LE
        //   "37000000" - Values[1]: 55 as int32 LE
        yield return new TestCaseData(
            new[] { "", "7A8B49" }, new[] { 0, 55 },
            "61" + "0200" + "0900" + "00" + "000000000000" + "0000" + "8B7A" + "0000" + "00C0" + "49" + "00000000" + "37000000"
        ).SetName("Variable_EmptyAndThreeBytes");

        // Three entries with varying separator lengths: 1, 2, 3 bytes.
        // No BaseOffset.
        //
        //   "61"         - Flags: NodeKind=Intermediate|KeyType=Variable|ValueSizeCode=10→4 bytes|LEKey
        //   "0300"       - KeyCount: 3
        //   "0D00"       - KeySize: 13 (3*2 prefixArr + 3*2 offsetArr + 1 remainingkeys)
        //   "00"         - CommonPrefixLen: 0
        //   "000000000000" - BaseOffset: 0
        //   "0041"       - prefixArr[0]: key "41" → LE-stored [00, 41]
        //   "4342"       - prefixArr[1]: key "4243" → LE-stored [43, 42]
        //   "4544"       - prefixArr[2]: key "444546" → LE-stored [45, 44]
        //   "0040"       - offsetArr[0]: tag=01, tailOffset=0; u16=0x4000 → LE [00, 40]
        //   "0080"       - offsetArr[1]: tag=10, tailOffset=0; u16=0x8000 → LE [00, 80]
        //   "00C0"       - offsetArr[2]: tag=11, tailOffset=0; u16=0xC000 → LE [00, C0]
        //   "46"         - remainingkeys: tail of entry 2 ("46")
        //   "00000000"   - Values[0]: 0 as int32 LE
        //   "64000000"   - Values[1]: 100 as int32 LE
        //   "C8000000"   - Values[2]: 200 as int32 LE
        yield return new TestCaseData(
            new[] { "41", "4243", "444546" }, new[] { 0, 100, 200 },
            "61" + "0300" + "0D00" + "00" + "000000000000" + "0041" + "4342" + "4544" + "0040" + "0080" + "00C0" + "46" + "00000000" + "64000000" + "C8000000"
        ).SetName("Variable_VaryingSeparators");
    }

    [TestCaseSource(nameof(VariableKeysTestCases))]
    public void IndexBuilder_VariableKeys_ProducesCorrectBinary(string[] separatorHexes, int[] values, string expectedHex)
    {
        byte[] output = new byte[1024];
        int keyBufSize = 0;
        for (int i = 0; i < separatorHexes.Length; i++) keyBufSize += 2 + separatorHexes[i].Length / 2;
        Span<byte> keyBuf = stackalloc byte[keyBufSize];
        SpanBufferWriter bufWriter = new(output);
        Span<byte> valScratch = stackalloc byte[separatorHexes.Length * (2 + 4)];
        BSearchIndexWriter<SpanBufferWriter> writer = new(ref bufWriter, new BSearchIndexMetadata { KeyType = 0 }, keyBuf, valScratch);
        Span<byte> valBuf = stackalloc byte[4];
        for (int i = 0; i < separatorHexes.Length; i++)
        {
            byte[] key = separatorHexes[i].Length > 0 ? Convert.FromHexString(separatorHexes[i]) : [];
            BinaryPrimitives.WriteInt32LittleEndian(valBuf, values[i]);
            writer.AddKey(key, valBuf);
        }
        writer.FinalizeNode();
        int written = (int)bufWriter.Written;

        Assert.That(Convert.ToHexString(output[..written]), Is.EqualTo(expectedHex));

        BSearchIndexReader index = BSearchIndexReader.ReadFromStart(output, 0);
        Assert.That(index.EntryCount, Is.EqualTo(separatorHexes.Length));
        Span<byte> fullKey = stackalloc byte[256];
        for (int i = 0; i < separatorHexes.Length; i++)
        {
            byte[] expectedSep = separatorHexes[i].Length > 0 ? Convert.FromHexString(separatorHexes[i]) : [];
            // Variable keys are LE-stored (prefix slot byte-reversed); GetFullKey reconstructs lex order.
            int written2 = index.GetFullKey(i, fullKey);
            Assert.That(fullKey[..written2].ToArray(), Is.EqualTo(expectedSep), $"Entry {i} separator mismatch");
        }
    }

    [Test]
    public void IndexBuilder_VariableKeys_TailRegionExceeds16KiB_Throws()
    {
        // SoA layout: tailOffset is 14 bits → remainingkeys cap is 16 KiB. With each entry
        // contributing (keyLen - 2) tail bytes, 80 entries × 256-byte keys → 80 × 254 = 20 320
        // tail bytes, well over 16 383.
        const int entries = 80;
        const int keyLen = 256;

        byte[] keyBuf = new byte[entries * (2 + keyLen)];
        byte[] valBufBig = new byte[entries * (2 + 4)];
        byte[] output = new byte[entries * (2 + keyLen) + 1024];
        SpanBufferWriter bufWriter = new(output);
        BSearchIndexWriter<SpanBufferWriter> writer = new(ref bufWriter, new BSearchIndexMetadata { KeyType = 0 }, keyBuf, valBufBig);
        Span<byte> valBuf = stackalloc byte[4];
        byte[] key = new byte[keyLen];
        for (int i = 0; i < entries; i++)
        {
            // Sort by varying byte 0 across i. Byte 0 differs between consecutive
            // entries → no common-prefix optimization; full key length is preserved.
            key[0] = (byte)i;
            BinaryPrimitives.WriteInt32LittleEndian(valBuf, i);
            writer.AddKey(key, valBuf);
        }

        InvalidOperationException? caught = null;
        try { writer.FinalizeNode(); }
        catch (InvalidOperationException ex) { caught = ex; }
        Assert.That(caught, Is.Not.Null, "Expected InvalidOperationException for 14-bit tailOffset overflow");
    }

    /// <summary>
    /// Mixed-tag fixture: one node with every <c>lenTag</c> value (0/1/2/3-byte and longer
    /// keys) plus a tail-bearing 50-byte and 255-byte entry. Exercises the prefix-padding
    /// path, sentinel-style tail-length derivation across short/long mixes, and the
    /// last-entry tail sentinel = remainingkeys.Length boundary.
    /// </summary>
    [Test]
    public void IndexBuilder_VariableKeys_MixedTagLengths_RoundTrip()
    {
        // Sorted by lex order: empty, 1-byte 0x05, 2-byte [0x05,0x05], 3-byte [0x05,0x05,0x05],
        // 50-byte 0x06.., 255-byte 0x07.. — covers every lenTag {00,01,10,11} plus tail growth.
        byte[][] keys =
        [
            [],
            [0x05],
            [0x05, 0x05],
            [0x05, 0x05, 0x05],
            BuildKey(50, 0x06),
            BuildKey(255, 0x07),
        ];

        byte[] keyBuf = new byte[keys.Sum(k => 2 + k.Length)];
        byte[] valScratch = new byte[keys.Length * (2 + 4)];
        byte[] output = new byte[4096];
        SpanBufferWriter bw = new(output);
        BSearchIndexWriter<SpanBufferWriter> writer = new(ref bw,
            new BSearchIndexMetadata { KeyType = 0 }, keyBuf, valScratch);
        Span<byte> valBuf = stackalloc byte[4];
        for (int i = 0; i < keys.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(valBuf, i * 11);
            writer.AddKey(keys[i], valBuf);
        }
        writer.FinalizeNode();

        BSearchIndexReader reader = BSearchIndexReader.ReadFromStart(output, 0);
        Assert.That(reader.EntryCount, Is.EqualTo(keys.Length));
        Assert.That(reader.Metadata.KeyType, Is.EqualTo(0));
        Assert.That(reader.Metadata.IsKeyLittleEndian, Is.True, "Variable keys are always LE-stored");

        // Round-trip via GetFullKey: lex-order bytes must match the original keys.
        Span<byte> dest = stackalloc byte[256];
        for (int i = 0; i < keys.Length; i++)
        {
            int written = reader.GetFullKey(i, dest);
            Assert.That(dest[..written].ToArray(), Is.EqualTo(keys[i]), $"Entry {i} key mismatch");
        }

        // Floor lookup hits the right entry / value for every key.
        for (int i = 0; i < keys.Length; i++)
        {
            Assert.That(reader.TryGetFloor(keys[i], out _, out ReadOnlySpan<byte> v), Is.True, $"Floor missing for entry {i}");
            Assert.That(BinaryPrimitives.ReadInt32LittleEndian(v), Is.EqualTo(i * 11));
        }

        // Inter-entry probes: a key longer than entry 1 but lex-equal to its prefix should
        // floor to entry 1 (not 2), since [0x05, 0x00] > [0x05] but < [0x05, 0x05].
        Assert.That(reader.TryGetFloor([0x05, 0x00], out _, out ReadOnlySpan<byte> v05_00), Is.True);
        Assert.That(BinaryPrimitives.ReadInt32LittleEndian(v05_00), Is.EqualTo(11), "Floor for [05,00] is entry 1 ([05])");

        static byte[] BuildKey(int len, byte fill)
        {
            byte[] k = new byte[len];
            Array.Fill(k, fill);
            return k;
        }
    }

    // ===== LEB128 TESTS =====

    [Test]
    public void Leb128_EncodedSize_CorrectForOffsets()
    {
        Assert.That(Leb128.EncodedSize(0), Is.EqualTo(1));
        Assert.That(Leb128.EncodedSize(127), Is.EqualTo(1));
        Assert.That(Leb128.EncodedSize(128), Is.EqualTo(2));
        Assert.That(Leb128.EncodedSize(16383), Is.EqualTo(2));
        Assert.That(Leb128.EncodedSize(16384), Is.EqualTo(3));
    }

    // ===== MULTI-LEVEL TREE TESTS =====

    [Test]
    public void MultiLevel_Tree_RootHasNodeChildren()
    {
        // Page-local nodes split when the next entry + estimated node body would
        // push past a 4 KiB page boundary. With 4-byte keys + 1-byte values
        // (~7 bytes per entry), ~230 entries fit in one page; bump well past that
        // to force multiple page-local nodes and a multi-level tree. The root's
        // first child is then itself a BSearchIndex node (Intermediate kind),
        // not an Entry — that's the format-level signal of multi-level structure.
        const int count = 500;
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
        {
            for (int i = 0; i < count; i++)
            {
                byte[] key = new byte[4];
                key[0] = (byte)(i >> 8);
                key[1] = (byte)(i & 0xFF);
                builder.Add(key, new byte[] { (byte)i });
            }
        });

        BSearchIndexReader rootIndex = ReadHsstRoot(data);
        // The root's leftmost child's flag byte should mark it as Intermediate
        // (a node), not Entry — proving the tree has multiple levels rather
        // than being a single leaf-level node with K entry children.
        ulong firstChildOffset = rootIndex.GetUInt64Value(0);
        byte firstChildFlag = data[firstChildOffset];
        BSearchNodeKind firstChildKind = (BSearchNodeKind)(firstChildFlag & 0x03);
        Assert.That(firstChildKind, Is.EqualTo(BSearchNodeKind.Intermediate));
    }

    [Test]
    public void FullHsst_AllKeysReachableViaIndex()
    {
        int count = 100;
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
        {
            for (int i = 0; i < count; i++)
            {
                byte[] key = new byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(key, i);
                builder.Add(key, System.BitConverter.GetBytes(i));
            }
        }, maxLeafEntries: 8);

        SpanByteReader reader = new(data);
        // Count entries via the new enumerator and verify each key is reachable via TrySeek.
        int actualCount = 0;
        using (HsstRefEnumerator<SpanByteReader, NoOpPin> e = new(in reader, new Bound(0, data.Length)))
        {
            while (e.MoveNext()) actualCount++;
        }
        Assert.That(actualCount, Is.EqualTo(count));

        for (int i = 0; i < count; i++)
        {
            byte[] key = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(key, i);
            using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
            Assert.That(r.TrySeek(key, out _), Is.True, $"Key {i} not found");
        }
    }

    // ===== COMMON-KEY-PREFIX OPTIMIZATION =====

    /// <summary>
    /// Build a Variable-key node manually so we can pin the on-disk effects
    /// of the common-prefix optimization (smaller node, prefix in metadata,
    /// flag bit 6, suffixes in keys section) and exercise the boundary-lookup
    /// branches in <see cref="BSearchIndexReader.TryGetFloor"/>.
    /// </summary>
    [TestCase(0, TestName = "CommonPrefix_Variable_NotInline")]
    [TestCase(1, TestName = "CommonPrefix_Uniform_NotInline")]
    public void CommonKeyPrefix_RoundTrip_AndBoundaryLookups(int keyType)
    {
        // 8 keys all sharing 4-byte prefix "DEADBEEF", then 1 differing byte.
        // Caller (mimicking HsstBTreeBuilder) decides the prefix and the layout
        // jointly, then passes both to the writer as construction options.
        string[] separatorHexes =
        [
            "DEADBEEF11", "DEADBEEF22", "DEADBEEF33", "DEADBEEF44",
            "DEADBEEF55", "DEADBEEF66", "DEADBEEF77", "DEADBEEF88",
        ];
        int[] values = [10, 20, 30, 40, 50, 60, 70, 80];

        // Hard-code the prefix here — this test pins the keyType to verify both
        // remaining layouts round-trip correctly under the option-driven writer.
        // Suffix length is 1.
        const int prefixLen = 4;
        byte[] commonPrefix = Convert.FromHexString("DEADBEEF");
        int slotSize = keyType == 1 ? 1 : 0;

        byte[] keyBuf = new byte[separatorHexes.Length * (2 + 1)];
        byte[] valScratch = new byte[separatorHexes.Length * (2 + 4)];
        byte[] output = new byte[1024];
        SpanBufferWriter w = new(output);
        // Production nodes drop the inline prefix bytes — the reader receives them via the
        // descending caller's parentSeparator parameter (sourced from the parent's separator
        // at descent, or from the HSST trailer for the root). This test passes commonPrefix
        // directly to ReadFromStart below to simulate that descent supply.
        BSearchIndexWriter<SpanBufferWriter> writer = new(ref w, new BSearchIndexMetadata
        {
            KeyType = keyType,
            KeySlotSize = slotSize,
        }, keyBuf, valScratch, commonPrefix);
        Span<byte> valBuf = stackalloc byte[4];
        for (int i = 0; i < separatorHexes.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(valBuf, values[i]);
            byte[] sep = Convert.FromHexString(separatorHexes[i]);
            writer.AddKey(sep.AsSpan(prefixLen), valBuf);
        }
        writer.FinalizeNode();
        int written = (int)w.Written;

        // Control node: same data without the prefix optimization (full-length keys,
        // no commonKeyPrefix passed). Demonstrates the size win.
        int controlSlotSize = keyType == 1 ? 5 : 0;
        byte[] controlKeyBuf = new byte[separatorHexes.Length * (2 + 5)];
        byte[] controlValScratch = new byte[separatorHexes.Length * (2 + 4)];
        byte[] controlOutput = new byte[1024];
        SpanBufferWriter cw = new(controlOutput);
        BSearchIndexWriter<SpanBufferWriter> controlWriter = new(ref cw, new BSearchIndexMetadata
        {
            KeyType = keyType,
            KeySlotSize = controlSlotSize,
        }, controlKeyBuf, controlValScratch);
        for (int i = 0; i < separatorHexes.Length; i++)
        {
            byte[] k = Convert.FromHexString(separatorHexes[i]);
            k[0] = (byte)i; // diverge at byte 0 → no shared prefix
            BinaryPrimitives.WriteInt32LittleEndian(valBuf, values[i]);
            controlWriter.AddKey(k, valBuf);
        }
        controlWriter.FinalizeNode();

        // Optimization paid off.
        Assert.That(written, Is.LessThan(cw.Written), "Common-prefix optimization should shrink the node");

        BSearchIndexReader reader = BSearchIndexReader.ReadFromStart(output, 0, commonPrefix);
        Assert.That(reader.CommonKeyPrefix.ToArray(), Is.EqualTo(Convert.FromHexString("DEADBEEF")));

        // Per-entry decoded suffix matches (suffix only, prefix stripped). GetFullKey
        // reconstructs lex order for all encodings.
        Span<byte> suffixBuf = stackalloc byte[16];
        for (int i = 0; i < separatorHexes.Length; i++)
        {
            byte[] expectedSuffix = [Convert.FromHexString(separatorHexes[i])[4]];
            int total = reader.GetFullKey(i, suffixBuf);
            int prefixLenInDest = reader.CommonKeyPrefix.Length;
            Assert.That(suffixBuf.Slice(prefixLenInDest, total - prefixLenInDest).ToArray(),
                Is.EqualTo(expectedSuffix), $"Suffix {i} mismatch");
        }

        // GetFullKey reconstructs the original key.
        Span<byte> reconstructed = stackalloc byte[16];
        for (int i = 0; i < separatorHexes.Length; i++)
        {
            int len = reader.GetFullKey(i, reconstructed);
            Assert.That(reconstructed[..len].ToArray(), Is.EqualTo(Convert.FromHexString(separatorHexes[i])));
        }

        // Floor lookup: exact, less-than-prefix, greater-than-prefix-non-matching.
        ReadOnlySpan<byte> probe = Convert.FromHexString("DEADBEEF44");
        Assert.That(reader.TryGetFloor(probe, out _, out ReadOnlySpan<byte> v44), Is.True);
        Assert.That(BinaryPrimitives.ReadInt32LittleEndian(v44), Is.EqualTo(40));

        // Probe < prefix (e.g. starts with 0x00) → no floor.
        Assert.That(reader.TryGetFloor(Convert.FromHexString("00FF"), out _, out _), Is.False);
        Assert.That(reader.FindFloorIndex(Convert.FromHexString("00FF")), Is.EqualTo(-1));

        // Probe > prefix and !StartsWith(prefix) (e.g. 0xFF…) → floor = last entry.
        Assert.That(reader.TryGetFloor(Convert.FromHexString("FF"), out _, out ReadOnlySpan<byte> vLast), Is.True);
        Assert.That(BinaryPrimitives.ReadInt32LittleEndian(vLast), Is.EqualTo(80));

        // Probe == prefix exactly → floor = first entry (smallest stored key starts with prefix).
        Assert.That(reader.TryGetFloor(Convert.FromHexString("DEADBEEF"), out _, out _), Is.False,
            "Empty suffix < every non-empty stored suffix → no floor");

        // Probe between two stored keys (DEADBEEF40 between …33 and …44) → floor = …33.
        Assert.That(reader.TryGetFloor(Convert.FromHexString("DEADBEEF40"), out _, out ReadOnlySpan<byte> vBetween), Is.True);
        Assert.That(BinaryPrimitives.ReadInt32LittleEndian(vBetween), Is.EqualTo(30));
    }

    /// <summary>
    /// Two-entry node where the savings would be exactly zero (1 byte prefix,
    /// 2 entries → savings = 1 × 1 − 1 = 0). The layout planner must gate the
    /// strip out and report <c>commonKeyPrefixLen = 0</c>.
    /// </summary>
    [Test]
    public void CommonKeyPrefix_SkippedWhenSavingsNotPositive()
    {
        byte[] sepBuffer = [0xAA, 0x01, 0xAA, 0x02];
        ReadOnlySpan<int> offsets = [0, 2];
        ReadOnlySpan<int> lengths = [2, 2];

        BSearchIndexLayoutPlanner.Plan(lengths, crossEntryLcp: 1, keyLength: 2,
            out int prefixLen, out int keyType, out int keySlotSize, out _);

        Assert.That(prefixLen, Is.EqualTo(0), "1-byte LCP × 1 saving entry − 1 metadata byte = 0; must not strip");
        // Same length, length > 0 → Uniform-2.
        Assert.That(keyType, Is.EqualTo(1));
        Assert.That(keySlotSize, Is.EqualTo(2));

        // Round-trip through the writer with the planner's decision.
        byte[] keyBuf = new byte[2 * (2 + 2)];
        byte[] valScratch = new byte[2 * (2 + 4)];
        byte[] output = new byte[64];
        SpanBufferWriter w = new(output);
        BSearchIndexWriter<SpanBufferWriter> writer = new(ref w, new BSearchIndexMetadata
        {
            KeyType = keyType,
            KeySlotSize = keySlotSize,
        }, keyBuf, valScratch);
        Span<byte> valBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(valBuf, 1);
        writer.AddKey(sepBuffer.AsSpan(0, 2), valBuf);
        BinaryPrimitives.WriteInt32LittleEndian(valBuf, 2);
        writer.AddKey(sepBuffer.AsSpan(2, 2), valBuf);
        writer.FinalizeNode();

        BSearchIndexReader reader = BSearchIndexReader.ReadFromStart(output, 0);
        Assert.That(reader.CommonKeyPrefix.Length, Is.EqualTo(0));
    }

    // ===== LITTLE-ENDIAN KEY STORAGE (Flags bit 5) =====

    /// <summary>
    /// Round-trip a Uniform LE-encoded leaf for keySize ∈ {2,4,8}: header bit 5 is set,
    /// raw on-disk slot bytes are byte-reversed, GetKey returns raw stored bytes,
    /// GetFullKey reconstructs the original lex bytes, and FindFloorIndex matches the
    /// BE baseline at every probe (including misses) with the SIMD path enabled and disabled.
    /// </summary>
    [TestCase(2)]
    [TestCase(4)]
    [TestCase(8)]
    public void Uniform_LittleEndian_RoundTripAndFloorAgreesWithBigEndian(int keySize)
    {
        const int count = 96; // exercises both SIMD batch and scalar tail at keySize=8 (8/iter)
        Random rng = new(42 + keySize);
        byte[][] keys = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            byte[] k = new byte[keySize];
            rng.NextBytes(k);
            keys[i] = k;
        }
        Array.Sort(keys, (a, b) => a.AsSpan().SequenceCompareTo(b));
        // Drop duplicates (would break sorted-order writes).
        List<byte[]> dedup = [keys[0]];
        for (int i = 1; i < count; i++)
            if (!keys[i].AsSpan().SequenceEqual(dedup[^1])) dedup.Add(keys[i]);
        keys = dedup.ToArray();
        int n = keys.Length;

        byte[] beOut = WriteUniform(keys, keySize, isLittleEndian: false);
        byte[] leOut = WriteUniform(keys, keySize, isLittleEndian: true);

        BSearchIndexReader beReader = BSearchIndexReader.ReadFromStart(beOut, 0);
        BSearchIndexReader leReader = BSearchIndexReader.ReadFromStart(leOut, 0);

        // Header flag bit.
        Assert.That(beReader.Metadata.IsKeyLittleEndian, Is.False);
        Assert.That(leReader.Metadata.IsKeyLittleEndian, Is.True);
        Assert.That((leOut[0] & 0x40), Is.EqualTo(0x40));

        // Raw stored slot bytes are byte-reversed under LE.
        int hdrUniform = HeaderSize(beReader);
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<byte> beSlot = beOut.AsSpan(hdrUniform + i * keySize, keySize);
            ReadOnlySpan<byte> leSlot = leOut.AsSpan(hdrUniform + i * keySize, keySize);
            byte[] reversed = new byte[keySize];
            for (int j = 0; j < keySize; j++) reversed[j] = beSlot[keySize - 1 - j];
            Assert.That(leSlot.ToArray(), Is.EqualTo(reversed), $"LE slot {i} should be byte-reversed BE slot");
        }

        // GetFullKey under LE recovers original lex bytes.
        Span<byte> dest = stackalloc byte[keySize];
        for (int i = 0; i < n; i++)
        {
            int len = leReader.GetFullKey(i, dest);
            Assert.That(len, Is.EqualTo(keySize));
            Assert.That(dest.ToArray(), Is.EqualTo(keys[i]), $"GetFullKey LE entry {i} should equal lex bytes");
        }

        // Floor-index agreement: hits at every stored key, hits between, miss-below, miss-above.
        // Sweep SIMD on and off — exercises both the AVX-512 linear scan and the scalar
        // binary-search fallback inside each UniformKeySearch.UniformN{LE,BE} method.
        bool simdWasOn = UniformKeySearch.Enabled;
        try
        {
            foreach (bool simd in new[] { false, true })
            {
                UniformKeySearch.Enabled = simd;
                for (int i = 0; i < n; i++)
                {
                    int beIdx = beReader.FindFloorIndex(keys[i]);
                    int leIdx = leReader.FindFloorIndex(keys[i]);
                    Assert.That(leIdx, Is.EqualTo(beIdx), $"Hit i={i} simd={simd}");
                    Assert.That(leIdx, Is.EqualTo(i));
                }
                // Below-first.
                byte[] below = new byte[keySize]; // all zeros — strictly less than first iff first != 0
                if (keys[0].AsSpan().SequenceCompareTo(below) > 0)
                {
                    Assert.That(leReader.FindFloorIndex(below), Is.EqualTo(beReader.FindFloorIndex(below)));
                    Assert.That(leReader.FindFloorIndex(below), Is.EqualTo(-1));
                }
                // Above-last.
                byte[] above = new byte[keySize];
                Array.Fill(above, (byte)0xFF);
                Assert.That(leReader.FindFloorIndex(above), Is.EqualTo(beReader.FindFloorIndex(above)));
                Assert.That(leReader.FindFloorIndex(above), Is.EqualTo(n - 1));
                // Search key longer than keySize (intermediate-node descent shape): pad with zero bytes.
                byte[] longProbe = new byte[keySize + 5];
                keys[n / 2].CopyTo(longProbe, 0);
                Assert.That(leReader.FindFloorIndex(longProbe), Is.EqualTo(beReader.FindFloorIndex(longProbe)),
                    $"Longer probe simd={simd}");
            }
        }
        finally
        {
            UniformKeySearch.Enabled = simdWasOn;
        }
    }

    /// <summary>
    /// LayoutPlanner auto-enables the LE flag for Uniform 2/4/8 only; non-eligible widths
    /// must opt out.
    /// </summary>
    [TestCase(2, 1, true, TestName = "Plan_LE_Uniform2")]
    [TestCase(4, 1, true, TestName = "Plan_LE_Uniform4")]
    [TestCase(8, 1, true, TestName = "Plan_LE_Uniform8")]
    [TestCase(3, 1, false, TestName = "Plan_LE_Uniform3_NotEligible")]
    [TestCase(16, 1, false, TestName = "Plan_LE_Uniform16_NotEligible")]
    public void LayoutPlanner_AutoEnablesLeFlag_OnlyForEligibleShapes(int keyLen, int expectedKeyType, bool expectedLe)
    {
        const int count = 4;
        byte[] buf = new byte[keyLen * count];
        Span<int> offsets = stackalloc int[count];
        Span<int> lengths = stackalloc int[count];
        for (int i = 0; i < count; i++)
        {
            offsets[i] = i * keyLen;
            lengths[i] = keyLen;
            // Distinct keys with no common prefix (high byte differs).
            buf[i * keyLen] = (byte)(i + 1);
        }
        BSearchIndexLayoutPlanner.Plan(lengths, crossEntryLcp: 0, keyLength: keyLen,
            out _, out int keyType, out _, out bool keyLittleEndian);
        Assert.That(keyType, Is.EqualTo(expectedKeyType));
        Assert.That(keyLittleEndian, Is.EqualTo(expectedLe));
    }

    // Build a `lengths` span for a [firstLen, otherLen, otherLen, …] separator profile.
    private static int[] BuildLengthsProfile(int firstLen, int otherLen, int count)
    {
        int[] lens = new int[count];
        lens[0] = firstLen;
        for (int i = 1; i < count; i++) lens[i] = otherLen;
        return lens;
    }

    /// <summary>
    /// lcp can take the full <c>crossEntryLcp</c> (clamped only by minLen, keyLength-1,
    /// and the MaxCommonKeyPrefixLen header field) because the builder pads each slot
    /// from the key's data section past the natural separator. The user-observed leaf
    /// (firstLen=4, others=5, crossEntryLcp=4, 105 entries) widens to an 8-byte slot and,
    /// after the 4-byte lcp strip, lands at SIMD-eligible Uniform slot=4. Last row
    /// exercises a tight-budget case (keyLength == minLen) where the keyLength-1 clamp
    /// binds and the snap can't reach a SIMD slot — proves we don't sacrifice lcp to
    /// chase SIMD.
    /// </summary>
    [TestCase(4, 5, 105, 4, 32, 4, 1, 4, true, TestName = "Plan_FullLcp_UserScenario_105Entries")]
    [TestCase(4, 5, 2, 10, 32, 8, 1, 2, true, TestName = "Plan_FullLcp_TwoEntries_ClampedByMinLen")]
    [TestCase(5, 6, 10, 5, 32, 5, 1, 4, true, TestName = "Plan_FullLcp_MinLen5_FirstShorter")]
    [TestCase(5, 5, 10, 5, 5, 4, 1, 1, false, TestName = "Plan_FullLcp_AllSameLen_TightBudget_NoSimd")]
    public void LayoutPlanner_FullLcpPlusUniformSnap(
        int firstLen, int otherLen, int count, int crossEntryLcp, int keyLength,
        int expectedLcp, int expectedKeyType, int expectedKeySlotSize, bool expectedLe)
    {
        int[] lengths = BuildLengthsProfile(firstLen, otherLen, count);
        BSearchIndexLayoutPlanner.Plan(lengths, crossEntryLcp, keyLength,
            out int lcp, out int keyType, out int keySlotSize, out bool keyLittleEndian);
        Assert.That(lcp, Is.EqualTo(expectedLcp));
        Assert.That(keyType, Is.EqualTo(expectedKeyType));
        Assert.That(keySlotSize, Is.EqualTo(expectedKeySlotSize));
        Assert.That(keyLittleEndian, Is.EqualTo(expectedLe));
    }

    /// <summary>
    /// Mixed-length suffix profiles (firstLen != otherLen) land in Uniform — the
    /// non-niche UWL branch is gone. The builder pads each slot from key data past the
    /// natural separator, so the slot can exceed the individual entry's tail without
    /// losing correctness. Profiles whose longest separator is ≤ 8 bytes are widened to
    /// an 8-byte slot (then snapped down by the lcp strip when one applies); the
    /// maxLen=9 row keeps a natural slot and the maxLen=10 row pins the
    /// <c>effMaxLen &gt; 8</c> boundary where mixed-length large suffixes fall to
    /// Variable rather than a bloated Uniform slot.
    /// </summary>
    [TestCase(5, 6, 10, 4, 32, 4, 1, 4, true, TestName = "Plan_Mixed_Widen6to8_LcpSnap4")]
    [TestCase(6, 7, 10, 4, 32, 4, 1, 4, true, TestName = "Plan_Mixed_Widen7to8_LcpSnap4")]
    [TestCase(7, 8, 10, 4, 32, 4, 1, 4, true, TestName = "Plan_Mixed_MaxLen8_LcpSnap4")]
    [TestCase(5, 7, 10, 0, 32, 0, 1, 8, true, TestName = "Plan_Mixed_Widen7to8_NoLcp_Snap8")]
    [TestCase(5, 6, 10, 0, 8, 0, 1, 8, true, TestName = "Plan_Mixed_Widen_KeyLength8_Snap8")]
    [TestCase(8, 9, 10, 1, 32, 1, 1, 8, true, TestName = "Plan_Mixed_EffMax8_UniformSnap8")]
    [TestCase(9, 10, 10, 0, 32, 0, 0, 0, true, TestName = "Plan_Mixed_EffMax10_FallsToVariable")]
    public void LayoutPlanner_MixedLength_LandsInUniformNotUwl(
        int firstLen, int otherLen, int count, int crossEntryLcp, int keyLength,
        int expectedLcp, int expectedKeyType, int expectedKeySlotSize, bool expectedLe)
    {
        int[] lengths = BuildLengthsProfile(firstLen, otherLen, count);
        BSearchIndexLayoutPlanner.Plan(lengths, crossEntryLcp, keyLength,
            out int lcp, out int keyType, out int keySlotSize, out bool keyLittleEndian);
        Assert.That(lcp, Is.EqualTo(expectedLcp));
        Assert.That(keyType, Is.EqualTo(expectedKeyType));
        Assert.That(keySlotSize, Is.EqualTo(expectedKeySlotSize));
        Assert.That(keyLittleEndian, Is.EqualTo(expectedLe));
    }

    /// <summary>
    /// Power-of-2 snap in the Uniform branch: when the post-strip budget
    /// (<c>keyLength - lcp</c>) accommodates a SIMD-eligible slot {2, 4, 8}, the
    /// planner enlarges the slot rather than dropping the strip — the extra bytes
    /// per entry are padded from key data. Rows cover the slot=3→4 upgrade with
    /// preserved lcp, plus snap targets 4 and 8 for larger natural lengths, plus
    /// the lcp=0 no-op case, plus a tight-budget case where no snap fits.
    /// </summary>
    [TestCase(4, 4, 10, 1, 5, 1, 4, true, TestName = "Plan_Snap_Slot3To4_KeepsLcp")]
    [TestCase(8, 8, 10, 5, 16, 5, 4, true, TestName = "Plan_Snap_Eff3_To4")]
    [TestCase(8, 8, 10, 3, 16, 3, 8, true, TestName = "Plan_Snap_Eff5_To8")]
    [TestCase(4, 4, 10, 0, 4, 0, 4, true, TestName = "Plan_Snap_NoStrip_Slot4Native")]
    [TestCase(3, 3, 10, 0, 3, 0, 3, false, TestName = "Plan_Snap_TightBudget_NoSimd")]
    public void LayoutPlanner_UniformSlot_SnapsToPowerOfTwo_WhenBudgetAllows(
        int firstLen, int otherLen, int count, int crossEntryLcp, int keyLength,
        int expectedLcp, int expectedKeySlotSize, bool expectedLe)
    {
        int[] lengths = BuildLengthsProfile(firstLen, otherLen, count);
        BSearchIndexLayoutPlanner.Plan(lengths, crossEntryLcp, keyLength,
            out int lcp, out int keyType, out int keySlotSize, out bool keyLittleEndian);
        Assert.That(keyType, Is.EqualTo(1), "Uniform expected for allSameLen profiles");
        Assert.That(lcp, Is.EqualTo(expectedLcp));
        Assert.That(keySlotSize, Is.EqualTo(expectedKeySlotSize));
        Assert.That(keyLittleEndian, Is.EqualTo(expectedLe));
    }

    /// <summary>
    /// <see cref="BSearchIndexLayoutPlanner.WidenedSlotWidth"/> buckets the longest
    /// separator into a SIMD-eligible {2,4,8} slot when the key-length budget allows,
    /// and returns the length unchanged when no widening applies (longer than 8 bytes,
    /// or the budget is too tight for the matching bucket).
    /// </summary>
    [TestCase(1, 33, 2, TestName = "Widen_1to2")]
    [TestCase(2, 33, 2, TestName = "Widen_2_StaysAt2")]
    [TestCase(3, 33, 4, TestName = "Widen_3to4")]
    [TestCase(4, 33, 4, TestName = "Widen_4_StaysAt4")]
    [TestCase(5, 33, 8, TestName = "Widen_5to8")]
    [TestCase(8, 33, 8, TestName = "Widen_8_StaysAt8")]
    [TestCase(9, 33, 9, TestName = "Widen_9_NoWidening")]
    [TestCase(20, 33, 20, TestName = "Widen_20_NoWidening")]
    [TestCase(5, 8, 8, TestName = "Widen_5to8_KeyLength8")]
    [TestCase(6, 7, 6, TestName = "Widen_6_BudgetTooTightFor8")]
    [TestCase(3, 3, 3, TestName = "Widen_3_BudgetTooTightFor4")]
    public void LayoutPlanner_WidenedSlotWidth_BucketsToSimdSlot(int maxLen, int keyLength, int expected)
        => Assert.That(BSearchIndexLayoutPlanner.WidenedSlotWidth(maxLen, keyLength), Is.EqualTo(expected));

    /// <summary>
    /// Cap-vs-MaxCommonKeyPrefixLen ordering: when both <c>crossEntryLcp</c> and
    /// <c>minLen - 1</c> exceed <see cref="BSearchIndexLayoutPlanner.MaxCommonKeyPrefixLen"/>,
    /// the planner clamps to that ceiling (128) and the savings gate keeps the strip.
    /// </summary>
    [Test]
    public void LayoutPlanner_LcpExceedsMaxCommonKeyPrefixLen_ClampedToCap()
    {
        const int count = 50;
        const int len = 256;
        int[] lengths = BuildLengthsProfile(len, len, count);
        BSearchIndexLayoutPlanner.Plan(lengths, crossEntryLcp: 200, keyLength: 256,
            out int lcp, out int keyType, out int keySlotSize, out _);
        Assert.That(lcp, Is.EqualTo(BSearchIndexLayoutPlanner.MaxCommonKeyPrefixLen));
        Assert.That(keyType, Is.EqualTo(1));
        Assert.That(keySlotSize, Is.EqualTo(len - BSearchIndexLayoutPlanner.MaxCommonKeyPrefixLen));
    }

    /// <summary>
    /// Backwards compatibility: a node written with IsKeyLittleEndian=false (the historical
    /// encoding) must keep parsing and answering FindFloorIndex correctly under the updated reader.
    /// </summary>
    [Test]
    public void BackwardsCompat_BigEndianStored_StillReadsAndSearches()
    {
        const int n = 32;
        byte[][] keys = new byte[n][];
        for (int i = 0; i < n; i++) keys[i] = [(byte)(i * 7), (byte)(i * 11), (byte)(i * 13), (byte)(i * 17)];
        Array.Sort(keys, (a, b) => a.AsSpan().SequenceCompareTo(b));

        byte[] beOut = WriteUniform(keys, 4, isLittleEndian: false);
        BSearchIndexReader r = BSearchIndexReader.ReadFromStart(beOut, 0);
        Assert.That(r.Metadata.IsKeyLittleEndian, Is.False);
        for (int i = 0; i < n; i++)
            Assert.That(r.FindFloorIndex(keys[i]), Is.EqualTo(i));
    }

    private static int HeaderSize(BSearchIndexReader r)
    {
        // Fixed 12-byte header. ValueSize is packed into Flags bits 3-4 and the prefix
        // bytes themselves are carried out-of-band via parentSeparator, not in the node.
        _ = r;
        return 12;
    }

    private static byte[] WriteUniform(byte[][] keys, int keySize, bool isLittleEndian)
    {
        int n = keys.Length;
        byte[] keyBuf = new byte[n * (2 + keySize)];
        byte[] valScratch = new byte[n * (2 + 4)];
        byte[] output = new byte[16 * 1024];
        SpanBufferWriter w = new(output);
        BSearchIndexWriter<SpanBufferWriter> writer = new(ref w, new BSearchIndexMetadata
        {
            KeyType = 1,
            KeySlotSize = keySize,
            IsKeyLittleEndian = isLittleEndian,
        }, keyBuf, valScratch);
        Span<byte> valBuf = stackalloc byte[4];
        for (int i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(valBuf, i);
            writer.AddKey(keys[i], valBuf);
        }
        writer.FinalizeNode();
        return output;
    }
}
