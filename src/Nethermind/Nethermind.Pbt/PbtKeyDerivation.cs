// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Collections;
using Nethermind.Core.Memory;
using Nethermind.Int256;

namespace Nethermind.Pbt;

/// <summary>
/// EIP-8297 leaf helpers: code chunkification and <c>BASIC_DATA</c> packing.
/// </summary>
public static class PbtKeyDerivation
{
    public const int BasicDataLeafKey = 0;
    public const int CodeHashLeafKey = 1;
    public const int DelegationLeafKey = 2;
    public const int HeaderStorageOffset = 64;
    /// <summary>The number of storage slots, from slot zero, an account keeps in its own subtree from <see cref="HeaderStorageOffset"/> on.</summary>
    public const int HeaderStorageSlots = 64;

    /// <summary>Size of one code chunk, which is one leaf value.</summary>
    public const int CodeChunkSize = 32;

    /// <summary>Code bytes one chunk carries after its leading PUSHDATA count.</summary>
    public const int CodeBytesPerChunk = CodeChunkSize - 1;

    private const int BasicDataCodeSizeOffset = 4;
    private const int BasicDataNonceOffset = 8;
    private const int BasicDataBalanceOffset = 16;

    private const int PushOffset = 95;
    private const byte Push1 = PushOffset + 1;
    private const byte Push32 = PushOffset + 32;

    /// <summary>
    /// Splits code into <see cref="CodeChunkSize"/>-byte chunks: one leading byte counting the chunk's
    /// leading PUSHDATA bytes (capped at 31) followed by 31 code bytes, zero-padded at the end.
    /// </summary>
    /// <returns>The chunks laid out back to back, so that a run of them can be written as one span; the caller disposes them.</returns>
    public static RefCountingMemory ChunkifyCode(ReadOnlySpan<byte> code)
    {
        int chunkCount = (int)CodeChunkCount(code.Length);
        RefCountingMemory chunks = PooledRefCountingMemoryProvider.Instance.Rent(chunkCount * CodeChunkSize);
        ChunkifyCode(code, chunks.GetSpan());
        return chunks;
    }

    internal static void ChunkifyCode(ReadOnlySpan<byte> code, Span<byte> chunks)
    {
        int chunkCount = (int)CodeChunkCount(code.Length);
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
            int start = i * CodeBytesPerChunk;
            Span<byte> chunk = chunks.Slice(i * CodeChunkSize, CodeChunkSize);
            chunk[0] = Math.Min(pushDataRemaining[start], (byte)31);
            code[start..Math.Min(start + CodeBytesPerChunk, code.Length)].CopyTo(chunk[1..]);
        }
    }

    /// <summary>The number of chunks <see cref="ChunkifyCode(ReadOnlySpan{byte})"/> splits <paramref name="codeSize"/> bytes of code into.</summary>
    public static long CodeChunkCount(long codeSize) => (codeSize + CodeBytesPerChunk - 1) / CodeBytesPerChunk;

    /// <summary>Copies the code bytes of chunk <paramref name="chunkId"/> back into <paramref name="code"/>, the whole code's buffer.</summary>
    public static void CopyChunkCode(ReadOnlySpan<byte> chunk, int chunkId, Span<byte> code)
    {
        int start = chunkId * CodeBytesPerChunk;
        chunk.Slice(1, Math.Min(CodeBytesPerChunk, code.Length - start)).CopyTo(code[start..]);
    }

    /// <summary>
    /// Packs the <c>BASIC_DATA</c> leaf: version (1B, always 0) at offset 0, code size (4B BE) at
    /// offset 4, nonce (8B BE) at offset 8, balance (16B BE) at offset 16. Bytes 1-3 are reserved.
    /// </summary>
    public static void PackBasicData(Span<byte> dest32, uint codeSize, in UInt256 nonce, in UInt256 balance)
    {
        dest32.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(dest32[BasicDataCodeSizeOffset..], codeSize);
        BinaryPrimitives.WriteUInt64BigEndian(dest32[BasicDataNonceOffset..], nonce.u0);
        BinaryPrimitives.WriteUInt64BigEndian(dest32[BasicDataBalanceOffset..], balance.u1);
        BinaryPrimitives.WriteUInt64BigEndian(dest32[(BasicDataBalanceOffset + sizeof(ulong))..], balance.u0);
    }

    /// <summary>Whether the version and reserved bytes of <paramref name="basicData"/> are zero, as <see cref="PackBasicData"/> writes them.</summary>
    public static bool IsCanonicalBasicData(ReadOnlySpan<byte> basicData) => basicData[..BasicDataCodeSizeOffset].IndexOfAnyExcept((byte)0) < 0;

    /// <summary>The big-endian code size bytes of a <c>BASIC_DATA</c> leaf.</summary>
    public static ReadOnlySpan<byte> BasicDataCodeSize(ReadOnlySpan<byte> basicData) => basicData.Slice(BasicDataCodeSizeOffset, sizeof(uint));

    /// <summary>The big-endian nonce bytes of a <c>BASIC_DATA</c> leaf.</summary>
    public static ReadOnlySpan<byte> BasicDataNonce(ReadOnlySpan<byte> basicData) => basicData.Slice(BasicDataNonceOffset, sizeof(ulong));

    /// <summary>The big-endian balance bytes of a <c>BASIC_DATA</c> leaf.</summary>
    public static ReadOnlySpan<byte> BasicDataBalance(ReadOnlySpan<byte> basicData) => basicData[BasicDataBalanceOffset..];

    public static uint ReadBasicDataCodeSize(ReadOnlySpan<byte> basicData) => BinaryPrimitives.ReadUInt32BigEndian(BasicDataCodeSize(basicData));

    /// <summary>Reads back the nonce and balance <see cref="PackBasicData"/> wrote.</summary>
    /// <remarks>The leaf holds 16 balance bytes, so a balance above 2^128 does not round-trip; no such account is reachable.</remarks>
    public static void UnpackBasicData(ReadOnlySpan<byte> basicData, out ulong nonce, out UInt256 balance)
    {
        nonce = BinaryPrimitives.ReadUInt64BigEndian(BasicDataNonce(basicData));
        balance = new UInt256(BasicDataBalance(basicData), isBigEndian: true);
    }
}
