// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core.Utils;
using Nethermind.State.Flat.BSearchIndex;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Unit tests for BSearchIndexReader (B-tree navigation) and BSearchIndexWriter (B-tree construction).
/// Hex fixture tests document the exact binary format of each node type.
/// </summary>
[TestFixture]
public class BSearchIndexTests
{
    // Read the root node from a full-HSST byte array. Trailer is [RootSize u16 LE][IndexType u8].
    private static BSearchIndexReader ReadHsstRoot(byte[] data)
    {
        int rootSize = data[data.Length - 3] | (data[data.Length - 2] << 8);
        int rootStart = data.Length - 3 - rootSize;
        return BSearchIndexReader.ReadFromStart(data, rootStart);
    }

    // ===== METADATA READING TESTS =====

    [Test]
    public void IndexMetadata_ReadFromEnd_MinimalNode()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) => { });

        BSearchIndexReader index = ReadHsstRoot(data);
        Assert.That(index.EntryCount, Is.EqualTo(0));
        Assert.That(index.IsIntermediate, Is.False);
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
        Assert.That(rootIndex.IsIntermediate, Is.False);
    }

    [Test]
    public void BSearchIndex_EmptyIndex_HandlesCorrectly()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) => { });

        BSearchIndexReader index = ReadHsstRoot(data);
        Assert.That(index.EntryCount, Is.EqualTo(0));
        Assert.That(index.IsIntermediate, Is.False);
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
        Assert.That(rootIndex.IsIntermediate, Is.False);
    }

    // ===== HEX FIXTURE TESTS: UNIFORM KEYS =====

    private static IEnumerable<TestCaseData> UniformKeysTestCases()
    {
        // Single entry: separator=0x41 ('A'), value=100, keyLen=1
        // Header sits at the front; keys section then values section follow.
        //
        // Expected binary layout (header fields are fixed-width LE; no LEB128):
        //   "0A"           - Flags: leaf(0)|KeyType=Uniform(02)|ValueType=Uniform(08)
        //   "0100"         - KeyCount: 1 (u16 LE)
        //   "0100"         - KeySize: 1 (u16 LE — fixed key length)
        //   "04"           - ValueSize: 4 (u8 — fixed value slot size, 1..8)
        //   "000000000000" - BaseOffset: 0 (mandatory 6-byte LE)
        //   "41"           - Keys[0]: separator byte 0x41 (Uniform, 1 byte)
        //   "64000000"     - Values[0]: 100 as int32 LE (test passes ValueSlotSize=4)
        yield return new TestCaseData(
            new[] { "41" }, new[] { 100 }, 1,
            "0A" + "0100" + "0100" + "04" + "000000000000" + "41" + "64000000"
        ).SetName("Uniform_SingleEntry");

        // Three entries: separators=[0x41,0x43,0x45], values=[0,100,200], keyLen=1
        // BaseOffset = 0 here (writer didn't strip it; test exercises the BSearchIndexWriter
        // with an explicit ValueSlotSize=4, so values stay 4-byte int32 LE).
        //
        //   "0A"           - Flags
        //   "0300"         - KeyCount: 3
        //   "0100"         - KeySize: 1
        //   "04"           - ValueSize: 4
        //   "000000000000" - BaseOffset: 0
        //   "41 43 45"     - Keys[0..2]
        //   "00000000"     - Values[0]: 0 as int32 LE
        //   "64000000"     - Values[1]: 100 as int32 LE
        //   "C8000000"     - Values[2]: 200 as int32 LE
        yield return new TestCaseData(
            new[] { "41", "43", "45" }, new[] { 0, 100, 200 }, 1,
            "0A" + "0300" + "0100" + "04" + "000000000000" + "41" + "43" + "45" + "00000000" + "64000000" + "C8000000"
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
        for (int i = 0; i < separatorHexes.Length; i++)
        {
            byte[] expectedSep = separatorHexes[i].Length > 0 ? Convert.FromHexString(separatorHexes[i]) : [];
            Assert.That(index.GetKey(i).ToArray(), Is.EqualTo(expectedSep), $"Entry {i} separator mismatch");
            Assert.That(index.GetUInt64Value(i), Is.EqualTo((ulong)values[i]), $"Entry {i} value mismatch");
        }
    }

    [Test]
    public void IndexBuilder_UniformKeys_WithBaseOffset()
    {
        // Three entries with values=[100,200,300]. Caller pre-subtracts baseOffset=100.
        // BaseOffset is mandatory (6 bytes LE).
        //
        //   "0A"           - Flags: leaf, Uniform keys, Uniform values
        //   "0300"         - KeyCount: 3
        //   "0100"         - KeySize: 1
        //   "04"           - ValueSize: 4 (u8)
        //   "640000000000" - BaseOffset: 100 (mandatory 6-byte LE)
        //   "41 43 45"     - Keys[0..2]
        //   "00000000"     - Values[0]: 100-100=0 as int32 LE
        //   "64000000"     - Values[1]: 200-100=100 as int32 LE
        //   "C8000000"     - Values[2]: 300-100=200 as int32 LE
        string expectedHex = "0A" + "0300" + "0100" + "04" + "640000000000" + "41" + "43" + "45" + "00000000" + "64000000" + "C8000000";

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
        // Empty first entry forces Variable key format.
        // No BaseOffset: min=0.
        //
        //   "08"       - Flags: leaf(0)|KeyType=Variable(00)|ValueType=Uniform(08)
        //   "0200"     - KeyCount: 2
        //   "0900"     - KeySize: 9 (3 data + 3*2 offsets)
        //   "04"       - ValueSize: 4 (u8)
        //   "000000000000" - BaseOffset: 0
        //   "7A8B49"   - Raw key bytes (entry 0 empty, entry 1 = 7A8B49)
        //   "0000"     - SentinelOffsets[0]: 0  — entry 0 starts at 0
        //   "0000"     - SentinelOffsets[1]: 0  — entry 1 starts at 0 (entry 0 had length 0)
        //   "0300"     - SentinelOffsets[2]: 3  — sentinel; entry 1 length = 3 - 0 = 3
        //   "00000000" - Values[0]: 0 as int32 LE
        //   "37000000" - Values[1]: 55 as int32 LE
        yield return new TestCaseData(
            new[] { "", "7A8B49" }, new[] { 0, 55 },
            "08" + "0200" + "0900" + "04" + "000000000000" + "7A8B49" + "0000" + "0000" + "0300" + "00000000" + "37000000"
        ).SetName("Variable_EmptyAndThreeBytes");

        // Three entries with varying separator lengths: 1, 2, 3 bytes.
        // No BaseOffset: min=0.
        //
        //   "08"         - Flags: leaf(0)|KeyType=Variable(00)|ValueType=Uniform(08)
        //   "0300"       - KeyCount: 3
        //   "0E00"       - KeySize: 14 (1+2+3 data + 4*2 offsets)
        //   "04"         - ValueSize: 4 (u8)
        //   "000000000000" - BaseOffset: 0
        //   "41"         - Key bytes for entry 0
        //   "4243"       - Key bytes for entry 1
        //   "444546"     - Key bytes for entry 2
        //   "0000"       - SentinelOffsets[0]: 0
        //   "0100"       - SentinelOffsets[1]: 1
        //   "0300"       - SentinelOffsets[2]: 3
        //   "0600"       - SentinelOffsets[3]: 6 (sentinel)
        //   "00000000"   - Values[0]: 0 as int32 LE
        //   "64000000"   - Values[1]: 100 as int32 LE
        //   "C8000000"   - Values[2]: 200 as int32 LE
        yield return new TestCaseData(
            new[] { "41", "4243", "444546" }, new[] { 0, 100, 200 },
            "08" + "0300" + "0E00" + "04" + "000000000000" + "41" + "4243" + "444546" + "0000" + "0100" + "0300" + "0600" + "00000000" + "64000000" + "C8000000"
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
        for (int i = 0; i < separatorHexes.Length; i++)
        {
            byte[] expectedSep = separatorHexes[i].Length > 0 ? Convert.FromHexString(separatorHexes[i]) : [];
            Assert.That(index.GetKey(i).ToArray(), Is.EqualTo(expectedSep), $"Entry {i} separator mismatch");
        }
    }

    [Test]
    public void IndexBuilder_VariableKeys_DataRegionExceeds64KiB_Throws()
    {
        // 256 entries of 256-byte keys → cumulative data offset crosses ushort.MaxValue.
        // Sentinel offsets: dataOffset(end) = 256 * 256 = 65 536 > 65 535.
        const int entries = 256;
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
        Assert.That(caught, Is.Not.Null, "Expected InvalidOperationException for u16 offset overflow");
    }

    // ===== HEX FIXTURE TESTS: UNIFORM-WITH-LEN KEYS =====

    private static IEnumerable<TestCaseData> UniformWithLenKeysTestCases()
    {
        // Three intermediate entries: [], [AABB], [CCDD] with values=[0,100,200], slotSize=3.
        // No BaseOffset: min=0.
        //
        // Slot layout: [key bytes (padded)][actual length as last byte]
        //
        //   "0D"       - Flags: intermediate(01)|KeyType=UniformWithLen(04)|ValueType=Uniform(08)
        //   "0300"     - KeyCount: 3
        //   "0300"     - KeySize: 3 (slot size)
        //   "04"       - ValueSize: 4 (u8)
        //   "000000000000" - BaseOffset: 0
        //   "000000"   - Slot[0]: empty key (padded), length=0
        //   "AABB02"   - Slot[1]: key=AABB, length=2
        //   "CCDD02"   - Slot[2]: key=CCDD, length=2
        //   "00000000" - Values[0]: 0 as int32 LE
        //   "64000000" - Values[1]: 100 as int32 LE
        //   "C8000000" - Values[2]: 200 as int32 LE
        yield return new TestCaseData(
            new[] { "", "AABB", "CCDD" }, new[] { 0, 100, 200 }, 3, true,
            "0D" + "0300" + "0300" + "04" + "000000000000" + "000000" + "AABB02" + "CCDD02" + "00000000" + "64000000" + "C8000000"
        ).SetName("UniformWithLen_ThreeIntermediateEntries");
    }

    [TestCaseSource(nameof(UniformWithLenKeysTestCases))]
    public void IndexBuilder_UniformWithLenKeys_ProducesCorrectBinary(string[] separatorHexes, int[] values, int slotSize, bool isIntermediate, string expectedHex)
    {
        byte[] output = new byte[1024];
        int keyBufSize = 0;
        for (int i = 0; i < separatorHexes.Length; i++) keyBufSize += 2 + separatorHexes[i].Length / 2;
        Span<byte> keyBuf = stackalloc byte[keyBufSize];
        SpanBufferWriter bufWriter = new(output);
        Span<byte> valScratch = stackalloc byte[separatorHexes.Length * (2 + 4)];
        BSearchIndexWriter<SpanBufferWriter> writer = new(ref bufWriter, new BSearchIndexMetadata { KeyType = 2, KeySlotSize = slotSize, IsIntermediate = isIntermediate }, keyBuf, valScratch);
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
        Assert.That(index.IsIntermediate, Is.EqualTo(isIntermediate));
        for (int i = 0; i < separatorHexes.Length; i++)
        {
            byte[] expectedSep = separatorHexes[i].Length > 0 ? Convert.FromHexString(separatorHexes[i]) : [];
            Assert.That(index.GetKey(i).ToArray(), Is.EqualTo(expectedSep), $"Entry {i} separator mismatch");
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
    public void MultiLevel_Tree_RootIsIntermediate()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
        {
            for (int i = 0; i < 20; i++)
            {
                byte[] key = new byte[4];
                key[0] = (byte)(i >> 8);
                key[1] = (byte)(i & 0xFF);
                builder.Add(key, new byte[] { (byte)i });
            }
        }, maxLeafEntries: 4);

        BSearchIndexReader rootIndex = ReadHsstRoot(data);
        Assert.That(rootIndex.IsIntermediate, Is.True);
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
    [TestCase(2, TestName = "CommonPrefix_UniformWithLen_NotInline")]
    public void CommonKeyPrefix_RoundTrip_AndBoundaryLookups(int keyType)
    {
        // 8 keys all sharing 4-byte prefix "DEADBEEF", then 1 differing byte.
        // Caller (mimicking HsstIndexBuilder) decides the prefix and the layout
        // jointly, then passes both to the writer as construction options.
        string[] separatorHexes =
        [
            "DEADBEEF11", "DEADBEEF22", "DEADBEEF33", "DEADBEEF44",
            "DEADBEEF55", "DEADBEEF66", "DEADBEEF77", "DEADBEEF88",
        ];
        int[] values = [10, 20, 30, 40, 50, 60, 70, 80];

        // Hard-code the prefix here — this test pins the keyType to verify all three
        // round-trip correctly under the option-driven writer. Suffix length is 1.
        const int prefixLen = 4;
        byte[] commonPrefix = Convert.FromHexString("DEADBEEF");
        int slotSize = keyType switch { 1 => 1, 2 => 1 + 1, _ => 0 };

        byte[] keyBuf = new byte[separatorHexes.Length * (2 + 1)];
        byte[] valScratch = new byte[separatorHexes.Length * (2 + 4)];
        byte[] output = new byte[1024];
        SpanBufferWriter w = new(output);
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
        int controlSlotSize = keyType switch { 1 => 5, 2 => 5 + 1, _ => 0 };
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

        BSearchIndexReader reader = BSearchIndexReader.ReadFromStart(output, 0);
        Assert.That(reader.Metadata.HasCommonKeyPrefix, Is.True);
        Assert.That(reader.CommonKeyPrefix.ToArray(), Is.EqualTo(Convert.FromHexString("DEADBEEF")));

        // Per-entry decoded suffix matches (suffix only, prefix stripped).
        for (int i = 0; i < separatorHexes.Length; i++)
        {
            byte[] expectedSuffix = [Convert.FromHexString(separatorHexes[i])[4]];
            Assert.That(reader.GetKey(i).ToArray(), Is.EqualTo(expectedSuffix), $"Suffix {i} mismatch");
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

        BSearchIndexLayoutPlanner.Plan(sepBuffer, offsets, lengths,
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
        Assert.That(reader.Metadata.HasCommonKeyPrefix, Is.False);
        Assert.That(reader.CommonKeyPrefix.Length, Is.EqualTo(0));
    }

    /// <summary>
    /// Branchless variant of FindFloorIndex must agree with the branchful one across
    /// all three KeyTypes and at every probe position (interior, boundary, miss).
    /// </summary>
    [TestCase(0, TestName = "Branchless_Variable")]
    [TestCase(1, TestName = "Branchless_Uniform")]
    [TestCase(2, TestName = "Branchless_UniformWithLen")]
    public void BranchlessSearch_AgreesWithBranchful(int keyType)
    {
        const int count = 64;
        int slotSize = keyType == 1 ? 4 : keyType == 2 ? 5 : 0;

        // Sorted, non-trivial 4-byte keys (Variable also gets 4-byte entries; LCP
        // detection in the writer is bypassed since we hand-construct here).
        byte[][] keys = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            byte[] k = [(byte)(i * 3 + 1), (byte)(i * 5 + 7), (byte)(i * 7 + 11), (byte)(i * 11 + 13)];
            keys[i] = k;
        }

        byte[] keyBuf = new byte[count * (2 + 4)];
        byte[] valScratch = new byte[count * (2 + 4)];
        byte[] output = new byte[8 * 1024];
        SpanBufferWriter w = new(output);
        BSearchIndexWriter<SpanBufferWriter> writer = new(ref w, new BSearchIndexMetadata
        {
            KeyType = keyType,
            KeySlotSize = slotSize,
        }, keyBuf, valScratch);
        Span<byte> valBuf = stackalloc byte[4];
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(valBuf, i);
            writer.AddKey(keys[i], valBuf);
        }
        writer.FinalizeNode();

        BSearchIndexReader reader = BSearchIndexReader.ReadFromStart(output, 0);

        // For each stored key plus a synthetic "between" probe, the two paths must agree.
        try
        {
            for (int i = 0; i < count; i++)
            {
                byte[] probe = keys[i];
                BSearchIndexReader.BranchlessSearch = false;
                int branchful = reader.FindFloorIndex(probe);
                BSearchIndexReader.BranchlessSearch = true;
                int branchless = reader.FindFloorIndex(probe);
                Assert.That(branchless, Is.EqualTo(branchful), $"Hit i={i}");
            }
            // Below-first miss.
            byte[] below = [0, 0, 0, 0];
            BSearchIndexReader.BranchlessSearch = false;
            int b1 = reader.FindFloorIndex(below);
            BSearchIndexReader.BranchlessSearch = true;
            int b2 = reader.FindFloorIndex(below);
            Assert.That(b2, Is.EqualTo(b1), "Below-first miss");
            // Above-last miss.
            byte[] above = [0xFF, 0xFF, 0xFF, 0xFF];
            BSearchIndexReader.BranchlessSearch = false;
            b1 = reader.FindFloorIndex(above);
            BSearchIndexReader.BranchlessSearch = true;
            b2 = reader.FindFloorIndex(above);
            Assert.That(b2, Is.EqualTo(b1), "Above-last miss");
        }
        finally { BSearchIndexReader.BranchlessSearch = false; }
    }

    // ===== LITTLE-ENDIAN KEY STORAGE (Flags bit 5) =====

    /// <summary>
    /// Round-trip a Uniform LE-encoded leaf for keySize ∈ {2,4,8}: header bit 5 is set,
    /// raw on-disk slot bytes are byte-reversed, GetKey returns raw stored bytes,
    /// GetFullKey reconstructs the original lex bytes, and FindFloorIndex matches the
    /// BE baseline at every probe (including misses) under both branchful and branchless
    /// search and with the SIMD path enabled and disabled.
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
        List<byte[]> dedup = new() { keys[0] };
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
        Assert.That((leOut[0] & 0x20), Is.EqualTo(0x20));

        // Raw stored slot bytes are byte-reversed under LE.
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<byte> beSlot = beReader.GetKey(i);
            ReadOnlySpan<byte> leSlot = leReader.GetKey(i);
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
        // Sweep three configurations: scalar branchful, scalar branchless, SIMD-on.
        bool simdWasOn = BSearchIndexReaderSimd.Enabled;
        bool branchlessWas = BSearchIndexReader.BranchlessSearch;
        try
        {
            foreach ((bool branchless, bool simd) in new[] { (false, false), (true, false), (false, true) })
            {
                BSearchIndexReader.BranchlessSearch = branchless;
                BSearchIndexReaderSimd.Enabled = simd;
                for (int i = 0; i < n; i++)
                {
                    int beIdx = beReader.FindFloorIndex(keys[i]);
                    int leIdx = leReader.FindFloorIndex(keys[i]);
                    Assert.That(leIdx, Is.EqualTo(beIdx), $"Hit i={i} branchless={branchless} simd={simd}");
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
                    $"Longer probe branchless={branchless} simd={simd}");
            }
        }
        finally
        {
            BSearchIndexReaderSimd.Enabled = simdWasOn;
            BSearchIndexReader.BranchlessSearch = branchlessWas;
        }
    }

    /// <summary>
    /// LayoutPlanner auto-enables the LE flag for Uniform 2/4/8 only; UniformWithLen and
    /// non-eligible Uniform widths must opt out.
    /// </summary>
    [TestCase(2, 1, true,  TestName = "Plan_LE_Uniform2")]
    [TestCase(4, 1, true,  TestName = "Plan_LE_Uniform4")]
    [TestCase(8, 1, true,  TestName = "Plan_LE_Uniform8")]
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
        BSearchIndexLayoutPlanner.Plan(buf, offsets, lengths,
            out _, out int keyType, out _, out bool keyLittleEndian);
        Assert.That(keyType, Is.EqualTo(expectedKeyType));
        Assert.That(keyLittleEndian, Is.EqualTo(expectedLe));
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
