// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.State.Pbt;

/// <summary>
/// The EIP-8297 tree-key derivation: zone stems, the account header embedding, storage-zone and
/// code-zone stems, code chunkification and <c>BASIC_DATA</c> packing.
/// </summary>
public static class PbtKeyDerivation
{
    public const int BasicDataLeafKey = 0;
    public const int CodeHashLeafKey = 1;
    public const int HeaderStorageOffset = 64;
    public const int CodeOffset = 128;
    public const int StemSubtreeWidth = 256;

    public const int AccountZone = 0;
    public const int CodeZone = 1;

    private const int ZoneBits = 4;
    private const int StorageAddressPrefixBits = 60;
    private const int StorageSuffixBits = 187;

    /// <summary>Number of code chunks embedded in the account header stem.</summary>
    public const int HeaderCodeChunks = StemSubtreeWidth - CodeOffset;

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

    /// <summary>Builds a non-storage stem: a 4-bit zone followed by the high 244 bits of <paramref name="digest"/>.</summary>
    public static Stem ZoneStem(int zone, ReadOnlySpan<byte> digest)
    {
        Span<byte> stem = stackalloc byte[Stem.Length];
        stem[0] = (byte)((zone << ZoneBits) | (digest[0] >> ZoneBits));
        for (int i = 1; i < Stem.Length; i++)
        {
            stem[i] = (byte)((digest[i - 1] << ZoneBits) | (digest[i] >> ZoneBits));
        }

        return new Stem(stem);
    }

    public static Stem AccountHeaderStem(Address address)
    {
        // local, not inline: a span over a returned-by-value hash dangles once its temp is reused
        ValueHash256 addressHash = AddressKeyHash(address);
        return ZoneStem(AccountZone, addressHash.Bytes);
    }

    public static bool IsHeaderSlot(in UInt256 slot) => slot < HeaderStorageOffset;

    /// <summary>Sub-index of a header-embedded storage slot (<paramref name="slot"/> must be below <see cref="HeaderStorageOffset"/>).</summary>
    public static byte HeaderSlotSubIndex(in UInt256 slot) => (byte)(HeaderStorageOffset + slot.u0);

    /// <summary>
    /// Builds the storage-zone stem for <paramref name="slot"/> (which must be at or above
    /// <see cref="HeaderStorageOffset"/>): the storage high bit, a 60-bit address prefix and a
    /// 187-bit suffix bound to the address and tree index.
    /// </summary>
    public static Stem StorageStem(Address address, in UInt256 slot, out byte subIndex)
    {
        subIndex = (byte)(slot.u0 & 0xFF);
        UInt256 treeIndex = slot >> 8;

        Span<byte> address32 = stackalloc byte[32];
        Address32(address, address32);
        ValueHash256 prefix = Blake3Hash.Hash(address32);

        Span<byte> suffixInput = stackalloc byte[64];
        address32.CopyTo(suffixInput);
        treeIndex.ToBigEndian(suffixInput[32..]);
        ValueHash256 suffix = Blake3Hash.Hash(suffixInput);

        Span<byte> stem = stackalloc byte[Stem.Length];
        stem[0] = 0x80;
        CopyBits(prefix.Bytes, StorageAddressPrefixBits, stem, 1);
        CopyBits(suffix.Bytes, StorageSuffixBits, stem, 1 + StorageAddressPrefixBits);
        return new Stem(stem);
    }

    /// <summary>Sub-index of a header-embedded code chunk (<paramref name="chunkId"/> must be below <see cref="HeaderCodeChunks"/>).</summary>
    public static byte HeaderCodeChunkSubIndex(int chunkId) => (byte)(CodeOffset + chunkId);

    /// <summary>
    /// Builds the code-zone stem for an overflow chunk (<paramref name="chunkId"/> at or above
    /// <see cref="HeaderCodeChunks"/>), content-addressed by <paramref name="codeHash"/>.
    /// </summary>
    public static Stem CodeOverflowStem(in ValueHash256 codeHash, int chunkId, out byte subIndex)
    {
        int overflow = chunkId - HeaderCodeChunks;
        subIndex = (byte)(overflow & 0xFF);

        Span<byte> input = stackalloc byte[64];
        codeHash.Bytes.CopyTo(input);
        BinaryPrimitives.WriteInt32BigEndian(input[60..], overflow >> 8);
        ValueHash256 digest = Blake3Hash.Hash(input);
        return ZoneStem(CodeZone, digest.Bytes);
    }

    /// <summary>
    /// Splits code into 32-byte chunks: one leading byte counting the chunk's leading PUSHDATA
    /// bytes (capped at 31) followed by 31 code bytes, zero-padded at the end.
    /// </summary>
    public static byte[][] ChunkifyCode(ReadOnlySpan<byte> code)
    {
        int chunkCount = (code.Length + 30) / 31;
        byte[][] chunks = new byte[chunkCount][];
        if (chunkCount == 0) return chunks;

        // pushDataRemaining[i] = how many PUSHDATA bytes remain from position i (0 when i is an opcode)
        byte[] pushDataRemaining = new byte[code.Length];
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
            byte[] chunk = new byte[32];
            chunk[0] = Math.Min(pushDataRemaining[start], (byte)31);
            ReadOnlySpan<byte> slice = code[start..Math.Min(start + 31, code.Length)];
            slice.CopyTo(chunk.AsSpan(1));
            chunks[i] = chunk;
        }

        return chunks;
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

    /// <summary>Copies the high <paramref name="bitCount"/> MSB-first bits of <paramref name="src"/> into <paramref name="dest"/> at <paramref name="destBitOffset"/> (dest must be zeroed).</summary>
    private static void CopyBits(ReadOnlySpan<byte> src, int bitCount, Span<byte> dest, int destBitOffset)
    {
        for (int i = 0; i < bitCount; i++)
        {
            if (((src[i >> 3] >> (7 - (i & 7))) & 1) != 0)
            {
                int destBit = destBitOffset + i;
                dest[destBit >> 3] |= (byte)(1 << (7 - (destBit & 7)));
            }
        }
    }
}
