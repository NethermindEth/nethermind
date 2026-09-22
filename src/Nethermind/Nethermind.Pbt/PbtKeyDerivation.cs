// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Pbt;

/// <summary>
/// EIP-8297 leaf helpers: the address key hash, code chunkification and <c>BASIC_DATA</c> packing.
/// </summary>
public static class PbtKeyDerivation
{
    public const int BasicDataLeafKey = 0;
    public const int CodeHashLeafKey = 1;
    public const int DelegationLeafKey = 2;
    public const int HeaderStorageOffset = 64;
    public const int StemSubtreeWidth = 256;

    public const int AccountZone = 0;
    public const int CodeZone = 1;

    /// <summary>Size of one code chunk, which is one leaf value.</summary>
    public const int CodeChunkSize = 32;

    private const int PushOffset = 95;
    private const byte Push1 = PushOffset + 1;
    private const byte Push32 = PushOffset + 32;

    private static void Address32(Address address, Span<byte> dest32)
    {
        dest32[..12].Clear();
        address.Bytes.CopyTo(dest32[12..]);
    }

    /// <summary>BLAKE3 of the 32-byte left-padded address; the flat account/storage column key.</summary>
    public static ValueHash256 AddressKeyHash(Address address)
    {
        Span<byte> address32 = stackalloc byte[32];
        Address32(address, address32);
        return Blake3Hash.Hash(address32);
    }

    /// <summary>
    /// Splits code into <see cref="CodeChunkSize"/>-byte chunks: one leading byte counting the chunk's
    /// leading PUSHDATA bytes (capped at 31) followed by 31 code bytes, zero-padded at the end.
    /// </summary>
    /// <returns>The chunks laid out back to back, so that a run of them can be written as one span.</returns>
    public static byte[] ChunkifyCode(ReadOnlySpan<byte> code)
    {
        int chunkCount = (code.Length + 30) / 31;
        byte[] chunks = new byte[chunkCount * CodeChunkSize];
        ChunkifyCode(code, chunks);
        return chunks;
    }

    internal static void ChunkifyCode(ReadOnlySpan<byte> code, Span<byte> chunks)
    {
        int chunkCount = (code.Length + 30) / 31;
        ArgumentOutOfRangeException.ThrowIfNotEqual(chunks.Length, chunkCount * CodeChunkSize);
        chunks.Clear();
        if (chunkCount == 0) return;

        // pushDataRemaining[i] = how many PUSHDATA bytes remain from position i (0 when i is an opcode)
        using ArrayPoolListRef<byte> pushDataRemaining = new(code.Length, code.Length);
        int pos = 0;
        while (pos < code.Length)
        {
            int pushBytes = code[pos] is >= Push1 and <= Push32 ? code[pos] - PushOffset : 0;
            pos++;
            for (int x = 0; x < pushBytes && pos + x < code.Length; x++)
            {
                pushDataRemaining[pos + x] = (byte)(pushBytes - x);
            }

            pos += pushBytes;
        }

        for (int i = 0; i < chunkCount; i++)
        {
            int start = i * 31;
            Span<byte> chunk = chunks.Slice(i * CodeChunkSize, CodeChunkSize);
            chunk[0] = Math.Min(pushDataRemaining[start], (byte)31);
            code[start..Math.Min(start + 31, code.Length)].CopyTo(chunk[1..]);
        }
    }

    /// <summary>
    /// Packs the <c>BASIC_DATA</c> leaf: version (1B, always 0) at offset 0, code size (4B BE) at
    /// offset 4, nonce (8B BE) at offset 8, balance (16B BE) at offset 16. Bytes 1-3 are reserved.
    /// </summary>
    public static void PackBasicData(Span<byte> dest32, uint codeSize, in UInt256 nonce, in UInt256 balance)
    {
        dest32.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(dest32[4..], codeSize);
        BinaryPrimitives.WriteUInt64BigEndian(dest32[8..], nonce.u0);
        BinaryPrimitives.WriteUInt64BigEndian(dest32[16..], balance.u1);
        BinaryPrimitives.WriteUInt64BigEndian(dest32[24..], balance.u0);
    }

    public static uint ReadBasicDataCodeSize(ReadOnlySpan<byte> basicData) => BinaryPrimitives.ReadUInt32BigEndian(basicData.Slice(4, 4));

    /// <summary>Reads back the nonce and balance <see cref="PackBasicData"/> wrote.</summary>
    /// <remarks>The leaf holds 16 balance bytes, so a balance above 2^128 does not round-trip; no such account is reachable.</remarks>
    public static void UnpackBasicData(ReadOnlySpan<byte> basicData, out ulong nonce, out UInt256 balance)
    {
        nonce = BinaryPrimitives.ReadUInt64BigEndian(basicData.Slice(8, sizeof(ulong)));
        balance = new UInt256(basicData[16..], isBigEndian: true);
    }
}
