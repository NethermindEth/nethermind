// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using Snappier;

namespace Nethermind.BeaconChain.Legacy.Era;

/// <summary>Reads a consensus-layer <c>.era</c> file: the blocks of one era and the state at its end.</summary>
/// <remarks>
/// Format: https://github.com/eth-clients/e2store-format-specs/blob/main/formats/era.md. The contents are untrusted,
/// so every length is bounded before anything is allocated for it. Not thread-safe.
/// </remarks>
internal sealed class EraFileReader(string path) : IDisposable
{
    private const ushort BlockEntry = 0x0001;
    private const ushort StateEntry = 0x0002;
    private const ushort SlotIndexEntry = 0x3269;
    private const int EntryHeaderLength = 8;

    /// <summary>A slot index of one entry: header, start slot, relative offset and count.</summary>
    private const int StateIndexLength = EntryHeaderLength + 3 * sizeof(ulong);

    private const int MaxBlockBytes = 1 << 25;
    private const int MaxStateBytes = 1 << 30;

    private readonly FileStream _file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20);

    /// <summary>Reads the serialized signed blocks in slot order.</summary>
    /// <exception cref="InvalidDataException">The file is malformed.</exception>
    public IEnumerable<byte[]> ReadBlocks()
    {
        _file.Position = 0;
        while (_file.Position < _file.Length)
        {
            (ushort type, long length) = ReadEntryHeader();
            if (type == BlockEntry)
                yield return ReadCompressed(length, MaxBlockBytes);
            else
                _file.Position += length;
        }
    }

    /// <summary>Reads the serialized state at the end of the era.</summary>
    /// <exception cref="InvalidDataException">The file is malformed.</exception>
    public byte[] ReadState()
    {
        long indexPosition = _file.Length - StateIndexLength;
        if (indexPosition < 0)
            throw Malformed("is too short to hold a state index");
        _file.Position = indexPosition;
        (ushort type, long length) = ReadEntryHeader();
        Span<byte> index = stackalloc byte[StateIndexLength - EntryHeaderLength];
        if (type != SlotIndexEntry || length != index.Length)
            throw Malformed("does not end with a state index");
        _file.ReadExactly(index);

        long offset = BinaryPrimitives.ReadInt64LittleEndian(index[sizeof(ulong)..]);
        if (offset >= 0 || offset < -indexPosition)
            throw Malformed("has a state offset outside the file");
        _file.Position = indexPosition + offset;
        (type, length) = ReadEntryHeader();
        return type == StateEntry ? ReadCompressed(length, MaxStateBytes) : throw Malformed("has no state at its state offset");
    }

    private (ushort Type, long Length) ReadEntryHeader()
    {
        if (_file.Length - _file.Position < EntryHeaderLength)
            throw Malformed("ends inside an entry header");
        Span<byte> header = stackalloc byte[EntryHeaderLength];
        _file.ReadExactly(header);
        long length = BinaryPrimitives.ReadUInt32LittleEndian(header[sizeof(ushort)..]);
        return _file.Length - _file.Position >= length
            ? (BinaryPrimitives.ReadUInt16LittleEndian(header), length)
            : throw Malformed("ends inside an entry");
    }

    private byte[] ReadCompressed(long length, int maxBytes)
    {
        if (length > maxBytes)
            throw Malformed($"has a {length}-byte entry, above the {maxBytes}-byte limit");
        byte[] compressed = new byte[length];
        _file.ReadExactly(compressed);

        using SnappyStream decompressor = new(new MemoryStream(compressed), CompressionMode.Decompress);
        using MemoryStream output = new(compressed.Length);
        byte[] buffer = new byte[1 << 16];
        int read;
        while ((read = decompressor.Read(buffer)) > 0)
        {
            if (output.Length + read > maxBytes)
                throw Malformed($"has an entry that decompresses above the {maxBytes}-byte limit");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private InvalidDataException Malformed(string problem) => new($"Era file {path} {problem}.");

    public void Dispose() => _file.Dispose();
}
