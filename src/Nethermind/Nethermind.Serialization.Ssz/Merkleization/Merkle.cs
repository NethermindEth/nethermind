// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz.Merkleization;

using SHA256 =
#if ZK_EVM
    Merkle.Sha256;
#else
    System.Security.Cryptography.SHA256;
#endif

public static partial class Merkle
{
    public static readonly UInt256[] ZeroHashes = new UInt256[64];

    private static void BuildZeroHashes()
    {
        Span<UInt256> concatenation = stackalloc UInt256[2];
        // ZeroHashes[0] will be UInt256.Zero
        for (int i = 1; i < 64; i++)
        {
            UInt256 previous = ZeroHashes[i - 1];
            MemoryMarshal.CreateSpan(ref previous, 1).CopyTo(concatenation[..1]);
            MemoryMarshal.CreateSpan(ref previous, 1).CopyTo(concatenation.Slice(1, 1));
            ZeroHashes[i] = new UInt256(SHA256.HashData(MemoryMarshal.Cast<UInt256, byte>(concatenation)));
        }
    }

    static Merkle() => BuildZeroHashes();

    public static int NextPowerOfTwoExponent(ulong v) => BitOperations.Log2(BitOperations.RoundUpToPowerOf2(v));

    private static UInt256 Compute(Span<UInt256> span) => MemoryMarshal.Cast<byte, UInt256>(SHA256.HashData(MemoryMarshal.Cast<UInt256, byte>(span)))[0];

    internal static UInt256 HashConcatenation(UInt256 left, UInt256 right, int level)
    {
        if (IsZeroHash(left, level) && IsZeroHash(right, level))
        {
            return ZeroHashes[level + 1];
        }

        Span<UInt256> concatenation = stackalloc UInt256[2];
        concatenation[0] = left;
        concatenation[1] = right;
        return Compute(concatenation);
    }

    private static bool IsZeroHash(UInt256 span, int level) => span.Equals(ZeroHashes[level]);

    public static void MixIn(ref UInt256 root, int value)
    {
        ulong v = value < 0 ? ulong.MaxValue : 0L;
        UInt256 lengthPart = new((ulong)value, v, v, v);
        root = HashConcatenation(root, lengthPart, 0);
    }

    // EIP-7495 mixes the static active_fields bitvector into the progressive container root.
    public static void MixInActiveFields(ref UInt256 root, ReadOnlySpan<byte> activeFields)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(activeFields.Length, 32, nameof(activeFields));

        Span<byte> chunk = stackalloc byte[32];
        chunk.Clear();
        activeFields.CopyTo(chunk);
        root = HashConcatenation(root, new UInt256(chunk), 0);
    }

    // EIP-7495 / EIP-7916 progressive merkleization keeps generalized indices stable across extensions.
    public static void MerkleizeProgressive(out UInt256 root, ReadOnlySpan<UInt256> chunks, ulong numLeaves = 1)
    {
        ArgumentOutOfRangeException.ThrowIfZero(numLeaves, nameof(numLeaves));

        if (chunks.Length == 0)
        {
            root = UInt256.Zero;
            return;
        }

        int rightChunkCount = (int)Math.Min((ulong)chunks.Length, Math.Min(numLeaves, (ulong)int.MaxValue));
        ReadOnlySpan<UInt256> leftChunks = chunks[rightChunkCount..];
        UInt256 left = UInt256.Zero;
        if (!leftChunks.IsEmpty)
        {
            MerkleizeProgressive(out left, leftChunks, checked(numLeaves * 4));
        }

        Merkleize(out UInt256 right, chunks[..rightChunkCount], numLeaves);
        root = HashConcatenation(left, right, 0);
    }

    public static void Merkleize(out UInt256 root, ReadOnlySpan<byte> value)
    {
        const int typeSize = 1;
        int partialChunkLength = value.Length % (32 / typeSize);
        if (partialChunkLength > 0)
        {
            ReadOnlySpan<byte> fullChunks = value[..^partialChunkLength];
            Span<byte> lastChunk = stackalloc byte[32 / typeSize];
            lastChunk.Clear();
            value[^partialChunkLength..].CopyTo(lastChunk);
            Merkleize(out root, MemoryMarshal.Cast<byte, UInt256>(fullChunks), MemoryMarshal.Cast<byte, UInt256>(lastChunk));
        }
        else
        {
            Merkleize(out root, MemoryMarshal.Cast<byte, UInt256>(value));
        }
    }

    public static void Merkleize(out UInt256 root, ReadOnlySpan<byte> value, ulong chunkCount)
    {
        const int typeSize = 1;
        int partialChunkLength = value.Length % (32 / typeSize);
        if (partialChunkLength > 0)
        {
            ReadOnlySpan<byte> fullChunks = value[..^partialChunkLength];
            Span<byte> lastChunk = stackalloc byte[32 / typeSize];
            lastChunk.Clear();
            value[^partialChunkLength..].CopyTo(lastChunk);
            Merkleize(out root, MemoryMarshal.Cast<byte, UInt256>(fullChunks), MemoryMarshal.Cast<byte, UInt256>(lastChunk), chunkCount);
        }
        else
        {
            Merkleize(out root, MemoryMarshal.Cast<byte, UInt256>(value), chunkCount);
        }
    }

    private static void Merkleize(out UInt256 root, ReadOnlySpan<UInt256> value, ReadOnlySpan<UInt256> lastChunk, ulong limit = 0)
    {
        if (limit == 0 && (value.Length + lastChunk.Length == 1))
        {
            root = value.Length == 0 ? lastChunk[0] : value[0];
            return;
        }

        int depth = NextPowerOfTwoExponent(limit == 0UL ? (uint)(value.Length + lastChunk.Length) : limit);
        Merkleizer merkleizer = new(depth);
        int length = value.Length;
        for (int i = 0; i < length; i++)
        {
            merkleizer.Feed(value[i]);
        }

        if (lastChunk.Length > 0)
        {
            merkleizer.Feed(lastChunk[0]);
        }

        merkleizer.CalculateRoot(out root);
    }

    public static void Merkleize(out UInt256 root, ReadOnlySpan<UInt256> value, ulong limit = 0UL)
    {
        if (limit == 0 && value.Length == 1)
        {
            root = value[0];
            return;
        }

        int depth = NextPowerOfTwoExponent(limit == 0UL ? (ulong)value.Length : limit);
        Merkleizer merkleizer = new(depth);
        int length = value.Length;
        for (int i = 0; i < length; i++)
        {
            merkleizer.Feed(value[i]);
        }

        merkleizer.CalculateRoot(out root);
    }

#if ZK_EVM
    internal static class Sha256
    {
        internal static byte[] HashData(ReadOnlySpan<byte> data)
        {
            byte[] output = new byte[System.Security.Cryptography.SHA256.HashSizeInBytes];

            Nethermind.Zkvm.Abstractions.Accelerators.Sha256(data, output);

            return output;
        }
    }
#endif
}
