// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Extensions;
using Snappier;

namespace Nethermind.Era1.Test;

internal class E2StoreWriterTests
{
    byte[] TestBytes = new byte[] { 0x0f, 0xf0, 0xff, 0xff };

    [TestCase(EntryTypes.Version)]
    [TestCase(EntryTypes.CompressedHeader)]
    [TestCase(EntryTypes.CompressedBody)]
    [TestCase(EntryTypes.CompressedReceipts)]
    [TestCase(EntryTypes.Accumulator)]
    [TestCase(EntryTypes.BlockIndex)]
    public async Task WriteEntry_WritingAnEntry_WritesCorrectHeaderType(ushort type)
    {
        using MemoryStream stream = new MemoryStream();
        using E2StoreWriter sut = new E2StoreWriter(stream);

        await sut.WriteEntry(type, Array.Empty<byte>());

        Assert.That(BinaryPrimitives.ReadInt16LittleEndian(stream.ToArray()), Is.EqualTo(type));
    }

    [TestCase(6)]
    [TestCase(20)]
    [TestCase(32)]
    public async Task WriteEntry_WritingAnEntry_WritesCorrectLengthInHeader(int length)
    {
        using MemoryStream stream = new MemoryStream();
        using E2StoreWriter sut = new E2StoreWriter(stream);

        await sut.WriteEntry(EntryTypes.CompressedHeader, new byte[length]);

        Assert.That(BinaryPrimitives.ReadInt32LittleEndian(stream.ToArray().Slice(2)), Is.EqualTo(length));
    }

    [TestCase(1)]
    [TestCase(5)]
    [TestCase(12)]
    public async Task WriteEntry_WritingAnEntry_ReturnCorrectNumberofBytesWritten(int length)
    {
        using MemoryStream stream = new MemoryStream();
        using E2StoreWriter sut = new E2StoreWriter(stream);

        int result = await sut.WriteEntry(EntryTypes.CompressedHeader, new byte[length]);

        Assert.That(result, Is.EqualTo(length + E2StoreWriter.HeaderSize));
    }


    [Test]
    public async Task WriteEntry_WritingAnEntry_ZeroesAtCorrectIndexesInHeader()
    {
        using MemoryStream stream = new MemoryStream();
        using E2StoreWriter sut = new E2StoreWriter(stream);

        await sut.WriteEntry(EntryTypes.CompressedHeader, TestBytes);
        byte[] bytes = stream.ToArray();

        Assert.That(bytes[6], Is.EqualTo(0));
        Assert.That(bytes[7], Is.EqualTo(0));
    }

    [Test]
    public async Task WriteEntry_WritingEntryValue_BytesAreWrittenToStream()
    {
        using MemoryStream stream = new MemoryStream();
        using E2StoreWriter sut = new E2StoreWriter(stream);

        await sut.WriteEntry(EntryTypes.CompressedHeader, TestBytes);
        byte[] result = stream.ToArray();

        Assert.That(new ArraySegment<byte>(result, E2StoreWriter.HeaderSize, TestBytes.Length), Is.EquivalentTo(TestBytes));
    }

    [Test]
    public async Task WriteEntryAsSnappy_WritingEntryValue_WritesEncodedBytesToStream()
    {
        using MemoryStream stream = new MemoryStream();
        using E2StoreWriter sut = new E2StoreWriter(stream);

        await sut.WriteEntryAsSnappy(EntryTypes.CompressedHeader, TestBytes);
        stream.Position = E2StoreWriter.HeaderSize;
        using var snappy = new SnappyStream(stream, System.IO.Compression.CompressionMode.Decompress);
        byte[] buffer = new byte[32];

        Assert.That(() => snappy.Read(buffer), Throws.Nothing);
    }

    [Test]
    public async Task WriteEntryAsSnappy_WritingEntryValue_ReturnsCompressedSize()
    {
        using MemoryStream stream = new MemoryStream();
        using E2StoreWriter sut = new E2StoreWriter(stream);

        int result = await sut.WriteEntryAsSnappy(EntryTypes.CompressedHeader, TestBytes);

        Assert.That(result, Is.EqualTo(stream.Length));
    }

    [Test]
    public async Task ReadEntryValue_ReadingValueBytesOfEntry_ReturnsBytesRead()
    {
        string tmpFile = Path.GetTempFileName();
        E2StoreWriter sut = new E2StoreWriter(File.OpenWrite(tmpFile));
        await sut.WriteEntry(EntryTypes.Accumulator, TestBytes);
        sut.Dispose();

        using E2StoreReader reader = new E2StoreReader(tmpFile);
        _ = reader.ReadEntryAndDecode(0, buf => buf.ToArray(), EntryTypes.Accumulator, out byte[] readBytes);
        Assert.That(readBytes, Is.EquivalentTo(TestBytes));
        Assert.That(readBytes.Length, Is.EqualTo(TestBytes.Length));
    }

    [Test]
    public async Task ReadEntryValueAsSnappy_ReadingValueBytesOfEntry_ReturnsDecompressedBytes()
    {
        string tmpFile = Path.GetTempFileName();
        using E2StoreWriter sut = new E2StoreWriter(File.OpenWrite(tmpFile));
        MemoryStream compressed = new();
        using SnappyStream snappy = new SnappyStream(compressed, System.IO.Compression.CompressionMode.Compress);
        snappy.Write(TestBytes);
        snappy.Flush();
        long position = sut.Position;
        await sut.WriteEntry(EntryTypes.CompressedHeader, compressed.ToArray());
        sut.Dispose();

        using E2StoreReader reader = new E2StoreReader(tmpFile);
        (var readBytes, _) = await reader.ReadSnappyCompressedEntryAndDecode<byte[]>(position, buf => buf.ToArray(), EntryTypes.CompressedHeader, default);
        Assert.That(readBytes, Is.EquivalentTo(TestBytes));
    }
}
