// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Snappier;

namespace Nethermind.Era1.Test;
internal class E2StoreTests
{
    [TestCase(EntryTypes.Version)]
    [TestCase(EntryTypes.CompressedHeader)]
    [TestCase(EntryTypes.CompressedBody)]
    [TestCase(EntryTypes.CompressedReceipts)]
    [TestCase(EntryTypes.Accumulator)]
    [TestCase(EntryTypes.BlockIndex)]
    public async Task WriteEntry_WritesCorrectHeader(ushort type)
    {
        MemoryStream stream = new MemoryStream();
        using E2Store sut = new E2Store(stream);

        await sut.WriteEntry(type, Array.Empty<byte>());

        Assert.That(BitConverter.ToInt16(stream.ToArray()) , Is.EqualTo(type)); 
    }

    [TestCase(6)]
    [TestCase(20)]
    [TestCase(32)]
    public async Task WriteEntry_WritesCorrectLengthInHeader(int length)
    {
        MemoryStream stream = new MemoryStream();
        using E2Store sut = new E2Store(stream);

        await sut.WriteEntry(EntryTypes.CompressedHeader, new byte[length]);

        Assert.That(BitConverter.ToInt32(stream.ToArray(), 2), Is.EqualTo(length));
    }

    [TestCase(1)]
    [TestCase(5)]
    [TestCase(12)]
    public async Task WriteEntry_ReturnCorrectNumberofBytesWritten(int length)
    {
        MemoryStream stream = new MemoryStream();
        using E2Store sut = new E2Store(stream);

        int result = await sut.WriteEntry(EntryTypes.CompressedHeader, new byte[length]);

        Assert.That(result, Is.EqualTo(length + E2Store.HeaderSize));
    }


    [Test]
    public async Task WriteEntry_ZeroesAtCorrectIndexesInHeader()
    {
        MemoryStream stream = new MemoryStream();
        using E2Store sut = new E2Store(stream);

        await sut.WriteEntry(EntryTypes.CompressedHeader, new byte[] { 0xff, 0xff, 0xff, 0xff });
        byte[] bytes = stream.ToArray();

        Assert.That(bytes[6], Is.EqualTo(0));
        Assert.That(bytes[7], Is.EqualTo(0));
    }

    [Test]
    public async Task WriteEntry_WritesBytesToStream()
    {
        MemoryStream stream = new MemoryStream();
        using E2Store sut = new E2Store(stream);

        byte[] bytes = new byte[] { 0x0f, 0xf0, 0xff, 0xff };
        await sut.WriteEntry(EntryTypes.CompressedHeader, bytes);
        byte[] result = stream.ToArray();
        
        Assert.That(new ArraySegment<byte>(result, E2Store.HeaderSize, bytes.Length), Is.EquivalentTo(bytes));
    }

    [Test]
    public async Task WriteEntryAsSnappy_WritesEncodedBytesToStream()
    {
        MemoryStream stream = new MemoryStream();
        using E2Store sut = new E2Store(stream);
        byte[] bytes = new byte[] { 0x0f, 0xf0, 0xff, 0xff };

        await sut.WriteEntryAsSnappy(EntryTypes.CompressedHeader, bytes);
        stream.Position = E2Store.HeaderSize;
        using var snappy = new SnappyStream(stream, System.IO.Compression.CompressionMode.Decompress);
        byte[] buffer = new byte[32];   

        Assert.That(() => snappy.Read(buffer), Throws.Nothing);
    }

    [Test]
    public async Task WriteEntryAsSnappy_ReturnsCompressedSize()
    {
        MemoryStream stream = new MemoryStream();
        using E2Store sut = new E2Store(stream);
        byte[] bytes = new byte[] { 0x0f, 0xf0, 0xff, 0xff };

        int result = await sut.WriteEntryAsSnappy(EntryTypes.CompressedHeader, bytes);

        Assert.That(result, Is.EqualTo(stream.Length));
    }

    [Test]
    public async Task ReadEntryValue_ReturnsCorrectValue()
    {
        MemoryStream stream = new MemoryStream();
        using E2Store sut = new E2Store(stream);
        byte[] bytes = new byte[] { 0x0f, 0xf0, 0xff, 0xff };
        await sut.WriteEntry(EntryTypes.Accumulator, bytes);
        byte[] buffer = new byte[bytes.Length];

        await sut.ReadEntryValue(buffer, new Entry(EntryTypes.Accumulator, 0, bytes.Length));

        Assert.That(buffer, Is.EquivalentTo(bytes));
    }

    [Test]
    public async Task ReadEntryValue_ReturnsBytesRead()
    {
        MemoryStream stream = new MemoryStream();
        using E2Store sut = new E2Store(stream);
        byte[] bytes = new byte[] { 0x0f, 0xf0, 0xff, 0xff };
        await sut.WriteEntry(EntryTypes.Accumulator, bytes);
        byte[] buffer = new byte[bytes.Length];

        int result = await sut.ReadEntryValue(buffer, new Entry(EntryTypes.Accumulator, 0, bytes.Length));

        Assert.That(result, Is.EqualTo(bytes.Length));
    }

    [Test]
    public async Task ReadEntryValueAsSnappy_ReturnsDecompressedBytes()
    {
        MemoryStream stream = new ();
        using E2Store sut = new E2Store(stream);
        byte[] bytes = new byte[] { 0x0f, 0xf0, 0xff, 0xff };
        MemoryStream compressed = new();
        using SnappyStream snappy = new SnappyStream(compressed, System.IO.Compression.CompressionMode.Compress);
        snappy.Write(bytes);
        snappy.Flush();
        byte[] compressedBytes = compressed.ToArray();
        await sut.WriteEntry(EntryTypes.CompressedHeader, compressedBytes);
        byte[] buffer = new byte[32];

        int read = await sut.ReadEntryValueAsSnappy(buffer, new Entry(EntryTypes.CompressedHeader, 0, compressedBytes.Length));

        Assert.That(new ArraySegment<byte>(buffer, 0, read), Is.EquivalentTo(bytes));
    }

    [Test]
    public async Task ReadValueAt_ReturnsCorrectValue()
    {
        MemoryStream stream = new();
        using E2Store sut = new E2Store(stream);
        byte[] bytes = new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff };
        stream.Write(bytes, 0, bytes.Length);

        long result = await sut.ReadValueAt(2);

        Assert.That(result, Is.EqualTo(BitConverter.ToInt64(bytes, 2)));
    }

    [TestCase(6)]
    [TestCase(14)]
    [TestCase(24)]
    public async Task ReadEntryAt_ReturnsCorrectEntry(int offset)
    {
        MemoryStream stream = new();
        using E2Store sut = new E2Store(stream);
        byte[] bytes = new byte[] { 0xff, 0xff, 0xff, 0xff};
        stream.SetLength(offset);
        stream.Seek(0, SeekOrigin.End);
        await sut.WriteEntry(EntryTypes.CompressedHeader, bytes);

        Entry result = await sut.ReadEntryAt(offset);

        Assert.That(result.Type, Is.EqualTo(EntryTypes.CompressedHeader));
        Assert.That(result.Length, Is.EqualTo(bytes.Length));
        Assert.That(result.Offset, Is.EqualTo(offset));
    }

    [TestCase(12)]
    [TestCase(20)]
    [TestCase(32)]
    public async Task ReadEntryHeaderAt_ReturnsCorrectEntryHeader(int offset)
    {
        MemoryStream stream = new();
        using E2Store sut = new E2Store(stream);
        byte[] bytes = new byte[] { 0xff, 0xff, 0xff, 0xff };
        stream.SetLength(offset);
        stream.Seek(0, SeekOrigin.End);
        await sut.WriteEntry(EntryTypes.CompressedHeader, bytes);

        HeaderData result = await sut.ReadEntryHeaderAt(offset);

        Assert.That(result.Type, Is.EqualTo(EntryTypes.CompressedHeader));
        Assert.That(result.Length, Is.EqualTo(bytes.Length));
    }


}
