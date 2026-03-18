// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Snappier;

namespace Nethermind.Era1;

/// <summary>
/// Writes Snappy framing format using block-format compression, producing output byte-identical
/// to golang/snappy's <c>BufferedWriter.Reset + Write + Flush</c> cycle used by go-ethereum's
/// era file writer. Each call to <see cref="Write"/> produces a self-contained framing stream
/// (stream identifier + one or more compressed chunks) suitable as an e2store entry payload.
/// </summary>
/// <remarks>
/// The standard <see cref="Snappier.SnappyStream"/> framing writer diverges from golang/snappy
/// by a small number of bytes, causing SHA-256 checksum mismatches with ethpandaops-exported
/// files. Using Snappier's block compressor (<c>Snappy.CompressToArray</c>) directly — which is
/// a faithful port of golang/snappy's <c>snappy.Encode</c> — ensures identical compressed bytes.
/// </remarks>
internal static class SnappyFrameWriter
{
    // Snappy framing format stream identifier: type=0xFF, length=6, data="sNaPpY"
    // https://github.com/google/snappy/blob/main/framing_format.txt
    private static ReadOnlySpan<byte> StreamIdentifier =>
        [0xff, 0x06, 0x00, 0x00, (byte)'s', (byte)'N', (byte)'a', (byte)'P', (byte)'p', (byte)'Y'];

    // Maximum uncompressed bytes per chunk — matches golang/snappy's maxBlockSize = 65536
    private const int MaxChunkSize = 65536;

    private const byte CompressedChunkType = 0x00;
    private const byte UncompressedChunkType = 0x01;

    /// <summary>
    /// Writes the snappy framing format encoding of <paramref name="uncompressed"/> to
    /// <paramref name="output"/>. Matches golang/snappy's <c>NewBufferedWriter + Reset + Write + Flush</c>.
    /// </summary>
    public static void Write(ReadOnlySpan<byte> uncompressed, Stream output)
    {
        output.Write(StreamIdentifier);

        for (int offset = 0; offset < uncompressed.Length; offset += MaxChunkSize)
        {
            ReadOnlySpan<byte> chunk = uncompressed.Slice(offset, Math.Min(MaxChunkSize, uncompressed.Length - offset));
            WriteChunk(chunk, output);
        }
    }

    private static void WriteChunk(ReadOnlySpan<byte> uncompressed, Stream output)
    {
        byte[] compressed = Snappy.CompressToArray(uncompressed);

        // golang/snappy uses uncompressed when compression saves less than 12.5% (same threshold)
        bool useCompressed = compressed.Length < uncompressed.Length - uncompressed.Length / 8;
        ReadOnlySpan<byte> data = useCompressed ? compressed : uncompressed;
        byte chunkType = useCompressed ? CompressedChunkType : UncompressedChunkType;

        // chunk data length = 4 (masked CRC32C) + data
        int chunkDataLength = 4 + data.Length;

        // Chunk header: type (1 byte) + length (3 bytes LE)
        Span<byte> header = stackalloc byte[4];
        header[0] = chunkType;
        header[1] = (byte)(chunkDataLength & 0xFF);
        header[2] = (byte)((chunkDataLength >> 8) & 0xFF);
        header[3] = (byte)((chunkDataLength >> 16) & 0xFF);
        output.Write(header);

        // Masked CRC32C of the uncompressed data (as per snappy framing spec)
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crcBytes, MaskedCrc32C(uncompressed));
        output.Write(crcBytes);

        output.Write(data);
    }

    private static uint MaskedCrc32C(ReadOnlySpan<byte> data)
    {
        uint crc = Crc32C(data);
        // golang/snappy masking: rotate right 15 bits, then add magic constant
        return ((crc >> 15) | (crc << 17)) + 0xa282ead8u;
    }

    // CRC-32C (Castagnoli) — reflected polynomial 0x82F63B78
    private static uint Crc32C(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
            crc = (crc >> 8) ^ s_crc32CTable[(byte)(crc ^ b)];
        return ~crc;
    }

    private static readonly uint[] s_crc32CTable = BuildCrc32CTable();

    private static uint[] BuildCrc32CTable()
    {
        uint[] table = new uint[256];
        const uint poly = 0x82F63B78u; // Castagnoli reflected polynomial
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? poly : 0);
            table[i] = crc;
        }
        return table;
    }
}
