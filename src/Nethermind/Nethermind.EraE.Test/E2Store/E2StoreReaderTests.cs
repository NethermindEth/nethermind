// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Test.IO;
using Nethermind.EraE.Exceptions;
using NUnit.Framework;
using E2StoreReader = Nethermind.EraE.E2Store.E2StoreReader;
using EntryTypes = Nethermind.EraE.E2Store.EntryTypes;

namespace Nethermind.EraE.Test.E2Store;

internal class E2StoreReaderTests
{
    private const int EntryHeaderSize = 8;

    [Test]
    public void ReadEntryAndDecode_WithCompleteEntry_DecodesPayload()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        using TempPath tmpFile = WriteEntry(EntryTypes.AccumulatorRoot, payload, 0);

        using E2StoreReader sut = new(tmpFile.Path);
        long nextPosition = sut.ReadEntryAndDecode(
            0, static buffer => buffer.ToArray(), EntryTypes.AccumulatorRoot, out byte[] value);

        Assert.That(value, Is.EqualTo(payload));
        Assert.That(nextPosition, Is.EqualTo(EntryHeaderSize + payload.Length));
    }

    [TestCase(1)]
    [TestCase(4)]
    [TestCase(8)]
    public void ReadEntryAndDecode_WhenPayloadTruncated_ThrowsEraFormatException(int missingBytes)
    {
        byte[] payload = new byte[32];
        using TempPath tmpFile = WriteEntry(EntryTypes.AccumulatorRoot, payload, missingBytes);

        using E2StoreReader sut = new(tmpFile.Path);
        Assert.That(
            () => sut.ReadEntryAndDecode(0, static buffer => buffer.ToArray(), EntryTypes.AccumulatorRoot, out _),
            Throws.TypeOf<EraFormatException>());
    }

    [Test]
    public void ReadEntryAndDecode_WhenPayloadTruncatedAfterOpening_ThrowsBeforeDecoding([Values(1, 4, 8)] int missingBytes)
    {
        byte[] payload = new byte[32];
        using TempPath tmpFile = WriteEntry(EntryTypes.AccumulatorRoot, payload, 0);
        using E2StoreReader sut = new(tmpFile.Path);

        using (FileStream file = new(tmpFile.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            file.SetLength(EntryHeaderSize + payload.Length - missingBytes);
        }

        bool decoderInvoked = false;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                () => sut.ReadEntryAndDecode(0, buffer =>
                {
                    decoderInvoked = true;
                    return buffer.ToArray();
                }, EntryTypes.AccumulatorRoot, out _),
                Throws.TypeOf<EraFormatException>());
            Assert.That(decoderInvoked, Is.False);
        }
    }

    [Test]
    public void ReadEntryAndDecode_WhenHeaderTruncated_ThrowsEraFormatException()
    {
        using TempPath tmpFile = WriteEntry(EntryTypes.AccumulatorRoot, [], 1);

        using E2StoreReader sut = new(tmpFile.Path);
        Assert.That(
            () => sut.ReadEntryAndDecode(0, static buffer => buffer.ToArray(), EntryTypes.AccumulatorRoot, out _),
            Throws.TypeOf<EraFormatException>());
    }

    [Test]
    public void ReadEntryAndDecode_WhenEntryHeaderPastEndOfFile_ThrowsEraFormatException()
    {
        using TempPath tmpFile = WriteEntry(EntryTypes.AccumulatorRoot, [0x01], 0);

        using E2StoreReader sut = new(tmpFile.Path);
        Assert.That(
            () => sut.ReadEntryAndDecode(1024, static buffer => buffer.ToArray(), EntryTypes.AccumulatorRoot, out _),
            Throws.TypeOf<EraFormatException>());
    }

    [TestCase(1)]
    [TestCase(8)]
    public void ReadSnappyCompressedEntryAndDecode_WhenPayloadTruncated_ThrowsEraFormatException(int missingBytes)
    {
        byte[] payload = new byte[64];
        using TempPath tmpFile = WriteEntry(EntryTypes.CompressedHeader, payload, missingBytes);

        using E2StoreReader sut = new(tmpFile.Path);
        Assert.That(
            async () =>
            {
                await sut.ReadSnappyCompressedEntryAndDecode(
                    0, static buffer => buffer.Length, EntryTypes.CompressedHeader);
            },
            Throws.TypeOf<EraFormatException>());
    }

    private static TempPath WriteEntry(ushort type, byte[] payload, int missingBytes)
    {
        TempPath tmpFile = TempPath.GetTempFile();
        byte[] entry = new byte[EntryHeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(entry, type);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(2), (uint)payload.Length);
        payload.CopyTo(entry, EntryHeaderSize);
        File.WriteAllBytes(tmpFile.Path, entry[..^missingBytes]);
        return tmpFile;
    }
}
