// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core.Utils;
using Nethermind.State.Flat.BSearchIndex;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;
using HsstReader = Nethermind.State.Flat.Hsst.Hsst;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Unit tests for BSearchIndexReader (B-tree navigation) and BSearchIndexWriter (B-tree construction).
/// Hex fixture tests document the exact binary format of each node type.
/// </summary>
[TestFixture]
public class BSearchIndexTests
{
    // ===== METADATA READING TESTS =====

    [Test]
    public void IndexMetadata_ReadFromEnd_MinimalNode()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> builder) => { });

        BSearchIndexReader index = BSearchIndexReader.ReadFromEnd(data, data.Length);
        Assert.That(index.EntryCount, Is.EqualTo(0));
        Assert.That(index.IsIntermediate, Is.False);
        Assert.That(index.Metadata.KeyCount, Is.EqualTo(0));
    }

    [Test]
    public void IndexMetadata_WithBaseOffset_ParsedCorrectly()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            for (int i = 0; i < 10; i++)
            {
                byte[] key = new byte[4];
                key[3] = (byte)i;
                builder.Add(key, new byte[] { (byte)i });
            }
        });

        BSearchIndexReader rootIndex = BSearchIndexReader.ReadFromEnd(data, data.Length);
        Assert.That(rootIndex.EntryCount, Is.EqualTo(10));
        Assert.That(rootIndex.IsIntermediate, Is.False);
    }

    [Test]
    public void BSearchIndex_EmptyIndex_HandlesCorrectly()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> builder) => { });

        BSearchIndexReader index = BSearchIndexReader.ReadFromEnd(data, data.Length);
        Assert.That(index.EntryCount, Is.EqualTo(0));
        Assert.That(index.IsIntermediate, Is.False);
        Assert.That(index.TryGetFloor("abc"u8, out _, out _), Is.False);
    }

    [Test]
    public void BSearchIndex_SingleLeafNode_StructureValid()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            builder.Add([0x41, 0x42], [0x01, 0x02, 0x03]);
        });

        BSearchIndexReader rootIndex = BSearchIndexReader.ReadFromEnd(data, data.Length);
        Assert.That(rootIndex.EntryCount, Is.EqualTo(1));
        Assert.That(rootIndex.IsIntermediate, Is.False);
    }

    // ===== HEX FIXTURE TESTS: UNIFORM KEYS =====

    private static IEnumerable<TestCaseData> UniformKeysTestCases()
    {
        // Single entry: separator=0x41 ('A'), value=100, keyLen=1
        //
        // Expected binary layout:
        //   "64000000" - Values[0]: 100 as int32 LE (no BaseOffset: min==max)
        //   "41"       - Keys[0]: separator byte 0x41 (Uniform, 1 byte)
        //   "0A"       - Metadata.Flags: leaf(0)|KeyType=Uniform(02)|ValueType=Uniform(08)
        //   "01"       - Metadata.KeyCount: 1 (LEB128)
        //   "01"       - Metadata.KeySize: 1 (fixed key length, LEB128)
        //   "04"       - Metadata.ValueSize: 4 (LEB128)
        //   "04"       - MetadataLength: 4 bytes
        yield return new TestCaseData(
            new[] { "41" }, new[] { 100 }, 1,
            "64000000" + "41" + "0A" + "01" + "01" + "04" + "04"
        ).SetName("Uniform_SingleEntry");

        // Three entries: separators=[0x41,0x43,0x45], values=[0,100,200], keyLen=1
        // No BaseOffset because min=0 (useBaseOffset requires min > 0).
        //
        //   "00000000" - Values[0]: 0 as int32 LE
        //   "64000000" - Values[1]: 100 as int32 LE
        //   "C8000000" - Values[2]: 200 as int32 LE
        //   "41"       - Keys[0]: 0x41
        //   "43"       - Keys[1]: 0x43
        //   "45"       - Keys[2]: 0x45
        //   "0A"       - Metadata.Flags: leaf, Uniform keys, Uniform values
        //   "03"       - Metadata.KeyCount: 3
        //   "01"       - Metadata.KeySize: 1
        //   "04"       - Metadata.ValueSize: 4
        //   "04"       - MetadataLength: 4 bytes
        yield return new TestCaseData(
            new[] { "41", "43", "45" }, new[] { 0, 100, 200 }, 1,
            "00000000" + "64000000" + "C8000000" + "41" + "43" + "45" + "0A" + "03" + "01" + "04" + "04"
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
        BSearchIndexWriter<SpanBufferWriter> writer = new(ref bufWriter, new BSearchIndexMetadata { KeyType = 1, KeySlotSize = keyLen }, keyBuf);
        Span<byte> valBuf = stackalloc byte[4];
        for (int i = 0; i < separatorHexes.Length; i++)
        {
            byte[] key = separatorHexes[i].Length > 0 ? Convert.FromHexString(separatorHexes[i]) : [];
            BinaryPrimitives.WriteInt32LittleEndian(valBuf, values[i]);
            writer.AddKey(key, valBuf);
        }
        writer.FinalizeNode();
        int written = bufWriter.Written;

        Assert.That(Convert.ToHexString(output[..written]), Is.EqualTo(expectedHex));

        // Also verify the reader parses the binary correctly
        BSearchIndexReader index = BSearchIndexReader.ReadFromEnd(output, written);
        Assert.That(index.EntryCount, Is.EqualTo(separatorHexes.Length));
        for (int i = 0; i < separatorHexes.Length; i++)
        {
            byte[] expectedSep = separatorHexes[i].Length > 0 ? Convert.FromHexString(separatorHexes[i]) : [];
            Assert.That(index.GetKey(i).ToArray(), Is.EqualTo(expectedSep), $"Entry {i} separator mismatch");
            Assert.That(index.GetIntValue(i), Is.EqualTo(values[i]), $"Entry {i} value mismatch");
        }
    }

    [Test]
    public void IndexBuilder_UniformKeys_WithBaseOffset()
    {
        // Three entries with values=[100,200,300]: min=100>0 and min<max triggers BaseOffset.
        // Caller computes baseOffset=100 and subtracts from values before Add.
        //
        //   "00000000" - Values[0]: 100-100=0 as int32 LE
        //   "64000000" - Values[1]: 200-100=100 as int32 LE
        //   "C8000000" - Values[2]: 300-100=200 as int32 LE
        //   "41"       - Keys[0]: 0x41
        //   "43"       - Keys[1]: 0x43
        //   "45"       - Keys[2]: 0x45
        //   "2A"       - Metadata.Flags: 0x0A|0x20 (HasBaseOffset bit set)
        //   "03"       - Metadata.KeyCount: 3
        //   "01"       - Metadata.KeySize: 1
        //   "04"       - Metadata.ValueSize: 4
        //   "64"       - Metadata.BaseOffset: 100
        //   "05"       - MetadataLength: 5 bytes
        string expectedHex = "00000000" + "64000000" + "C8000000" + "41" + "43" + "45" + "2A" + "03" + "01" + "04" + "64" + "05";

        int baseOffset = 100;
        byte[] output = new byte[1024];
        Span<byte> keyBuf = stackalloc byte[3 * (2 + 1)]; // 3 entries, each key is 1 byte
        SpanBufferWriter bufWriter = new(output);
        BSearchIndexWriter<SpanBufferWriter> writer = new(ref bufWriter, new BSearchIndexMetadata { KeyType = 1, KeySlotSize = 1, BaseOffset = baseOffset }, keyBuf);
        Span<byte> valBuf = stackalloc byte[4];
        foreach ((string sepHex, int val) in new[] { ("41", 100), ("43", 200), ("45", 300) })
        {
            BinaryPrimitives.WriteInt32LittleEndian(valBuf, val - baseOffset);
            writer.AddKey(Convert.FromHexString(sepHex), valBuf);
        }
        writer.FinalizeNode();
        int written = bufWriter.Written;

        Assert.That(Convert.ToHexString(output[..written]), Is.EqualTo(expectedHex));

        BSearchIndexReader index = BSearchIndexReader.ReadFromEnd(output, written);
        Assert.That(index.Metadata.BaseOffset, Is.EqualTo(100));
        Assert.That(index.GetIntValue(0), Is.EqualTo(100));
        Assert.That(index.GetIntValue(1), Is.EqualTo(200));
        Assert.That(index.GetIntValue(2), Is.EqualTo(300));
    }

    // ===== HEX FIXTURE TESTS: VARIABLE KEYS =====

    private static IEnumerable<TestCaseData> VariableKeysTestCases()
    {
        // Two entries: empty separator + "7A8B49" (3 bytes).
        // Empty first entry forces Variable key format.
        // No BaseOffset: min=0.
        //
        //   "00000000" - Values[0]: 0 as int32 LE
        //   "37000000" - Values[1]: 55 as int32 LE
        //   "0000"     - OffsetTable[0]: 0 (u16 LE) — entry 0 key data starts at offset 0
        //   "0100"     - OffsetTable[1]: 1 (u16 LE) — entry 1 key data starts at offset 1
        //   "00"       - LEB128(0): separator length 0 (entry 0, empty)
        //   "03"       - LEB128(3): separator length 3 (entry 1)
        //   "7A8B49"   - Key bytes for entry 1
        //   "08"       - Metadata.Flags: leaf(0)|KeyType=Variable(00)|ValueType=Uniform(08)
        //   "02"       - Metadata.KeyCount: 2
        //   "09"       - Metadata.KeySize: 9 (total Keys section size for Variable)
        //   "04"       - Metadata.ValueSize: 4
        //   "04"       - MetadataLength: 4 bytes
        yield return new TestCaseData(
            new[] { "", "7A8B49" }, new[] { 0, 55 },
            "00000000" + "37000000" + "0000" + "0100" + "00" + "03" + "7A8B49" + "08" + "02" + "09" + "04" + "04"
        ).SetName("Variable_EmptyAndThreeBytes");

        // Three entries with varying separator lengths: 1, 2, 3 bytes.
        // This is the HSST equivalent of RSST's "Variable_VaryingSeparators".
        // No BaseOffset: min=0.
        //
        //   "00000000"   - Values[0]: 0 as int32 LE
        //   "64000000"   - Values[1]: 100 as int32 LE
        //   "C8000000"   - Values[2]: 200 as int32 LE
        //   "0000"       - OffsetTable[0]: 0 (u16 LE)
        //   "0200"       - OffsetTable[1]: 2 (u16 LE) — after LEB128(1)+1 = 2 bytes
        //   "0500"       - OffsetTable[2]: 5 (u16 LE) — after 2 + LEB128(2)+2 = 5 bytes
        //   "01"         - LEB128(1): separator length 1 (entry 0)
        //   "41"         - Key bytes for entry 0
        //   "02"         - LEB128(2): separator length 2 (entry 1)
        //   "4243"       - Key bytes for entry 1
        //   "03"         - LEB128(3): separator length 3 (entry 2)
        //   "444546"     - Key bytes for entry 2
        //   "08"         - Metadata.Flags: leaf(0)|KeyType=Variable(00)|ValueType=Uniform(08)
        //   "03"         - Metadata.KeyCount: 3
        //   "0F"         - Metadata.KeySize: 15 (total Keys section: 6 offset table + 2+3+4 data)
        //   "04"         - Metadata.ValueSize: 4
        //   "04"         - MetadataLength: 4 bytes
        yield return new TestCaseData(
            new[] { "41", "4243", "444546" }, new[] { 0, 100, 200 },
            "0000000064000000C8000000" + "0000" + "0200" + "0500" + "01" + "41" + "02" + "4243" + "03" + "444546" + "08" + "03" + "0F" + "04" + "04"
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
        BSearchIndexWriter<SpanBufferWriter> writer = new(ref bufWriter, new BSearchIndexMetadata { KeyType = 0 }, keyBuf);
        Span<byte> valBuf = stackalloc byte[4];
        for (int i = 0; i < separatorHexes.Length; i++)
        {
            byte[] key = separatorHexes[i].Length > 0 ? Convert.FromHexString(separatorHexes[i]) : [];
            BinaryPrimitives.WriteInt32LittleEndian(valBuf, values[i]);
            writer.AddKey(key, valBuf);
        }
        writer.FinalizeNode();
        int written = bufWriter.Written;

        Assert.That(Convert.ToHexString(output[..written]), Is.EqualTo(expectedHex));

        BSearchIndexReader index = BSearchIndexReader.ReadFromEnd(output, written);
        Assert.That(index.EntryCount, Is.EqualTo(separatorHexes.Length));
        for (int i = 0; i < separatorHexes.Length; i++)
        {
            byte[] expectedSep = separatorHexes[i].Length > 0 ? Convert.FromHexString(separatorHexes[i]) : [];
            Assert.That(index.GetKey(i).ToArray(), Is.EqualTo(expectedSep), $"Entry {i} separator mismatch");
        }
    }

    // ===== HEX FIXTURE TESTS: UNIFORM-WITH-LEN KEYS =====

    private static IEnumerable<TestCaseData> UniformWithLenKeysTestCases()
    {
        // Three intermediate entries: [], [AABB], [CCDD] with values=[0,100,200], slotSize=3.
        // No BaseOffset: min=0.
        //
        // Slot layout: [key bytes (padded)][actual length as last byte]
        //
        //   "00000000" - Values[0]: 0 as int32 LE
        //   "64000000" - Values[1]: 100 as int32 LE
        //   "C8000000" - Values[2]: 200 as int32 LE
        //   "000000"   - Slot[0]: empty key (padded), length=0
        //   "AABB02"   - Slot[1]: key=AABB, length=2
        //   "CCDD02"   - Slot[2]: key=CCDD, length=2
        //   "0D"       - Metadata.Flags: intermediate(01)|KeyType=UniformWithLen(04)|ValueType=Uniform(08)
        //   "03"       - Metadata.KeyCount: 3
        //   "03"       - Metadata.KeySize: 3 (slot size)
        //   "04"       - Metadata.ValueSize: 4
        //   "04"       - MetadataLength: 4 bytes
        yield return new TestCaseData(
            new[] { "", "AABB", "CCDD" }, new[] { 0, 100, 200 }, 3, true,
            "00000000" + "64000000" + "C8000000" + "000000" + "AABB02" + "CCDD02" + "0D" + "03" + "03" + "04" + "04"
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
        BSearchIndexWriter<SpanBufferWriter> writer = new(ref bufWriter, new BSearchIndexMetadata { KeyType = 2, KeySlotSize = slotSize, IsIntermediate = isIntermediate }, keyBuf);
        Span<byte> valBuf = stackalloc byte[4];
        for (int i = 0; i < separatorHexes.Length; i++)
        {
            byte[] key = separatorHexes[i].Length > 0 ? Convert.FromHexString(separatorHexes[i]) : [];
            BinaryPrimitives.WriteInt32LittleEndian(valBuf, values[i]);
            writer.AddKey(key, valBuf);
        }
        writer.FinalizeNode();
        int written = bufWriter.Written;

        Assert.That(Convert.ToHexString(output[..written]), Is.EqualTo(expectedHex));

        BSearchIndexReader index = BSearchIndexReader.ReadFromEnd(output, written);
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
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            for (int i = 0; i < 20; i++)
            {
                byte[] key = new byte[4];
                key[0] = (byte)(i >> 8);
                key[1] = (byte)(i & 0xFF);
                builder.Add(key, new byte[] { (byte)i });
            }
        }, maxLeafEntries: 4);

        BSearchIndexReader rootIndex = BSearchIndexReader.ReadFromEnd(data, data.Length);
        Assert.That(rootIndex.IsIntermediate, Is.True);
    }

    [Test]
    public void FullHsst_AllKeysReachableViaIndex()
    {
        int count = 100;
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            for (int i = 0; i < count; i++)
            {
                byte[] key = new byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(key, i);
                builder.Add(key, System.BitConverter.GetBytes(i));
            }
        }, maxLeafEntries: 8);

        HsstReader hsst = new(data);
        Assert.That(hsst.EntryCount, Is.EqualTo(count));

        for (int i = 0; i < count; i++)
        {
            byte[] key = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(key, i);
            Assert.That(hsst.TryGet(key, out _), Is.True, $"Key {i} not found");
        }
    }
}
