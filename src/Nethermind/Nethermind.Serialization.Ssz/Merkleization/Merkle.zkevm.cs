// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz.Merkleization;

public static partial class Merkle
{
    // SHA-256 initial hash values (FIPS 180-4), stored as the [u64;4] layout syscall_sha256_f expects.
    private static readonly ulong[] Sha256Init =
    [
        0x6a09e667 | ((ulong)0xbb67ae85 << 32),
        0x3c6ef372 | ((ulong)0xa54ff53a << 32),
        0x510e527f | ((ulong)0x9b05688c << 32),
        0x1f83d9ab | ((ulong)0x5be0cd19 << 32),
    ];

    // The second compression block of any 64-byte message is constant: 0x80 terminator,
    // zeros, and the 512-bit message length in the trailing eight big-endian bytes.
    private static readonly ulong[] Sha256PadFor64 = [0x80, 0, 0, 0, 0, 0, 0, 0x0002_0000_0000_0000];

    /// <summary>Executes the SHA-256 extend-and-compress precompile on the given state and block.</summary>
    /// <remarks>
    /// TODO: move next to <c>syscall_keccak_f</c> in Nethermind.Zkvm.Abstractions once the standard
    /// gains it: https://github.com/eth-act/zkvm-standards/issues/23
    /// </remarks>
    [LibraryImport("__Internal")]
    private static unsafe partial void syscall_sha256_f(void** parameters);

    /// <summary>Hashes the 64-byte concatenation of two chunks with SHA-256 via the compression precompile.</summary>
    /// <remarks>
    /// The dominant merkleization hash: two precompile compressions bracketed by plain field moves.
    /// Going through <c>zkvm_sha256</c> instead costs an output allocation, P/Invoke marshalling, and
    /// a wrapper that re-derives the padding block this shape makes constant.
    /// </remarks>
    [SkipLocalsInit]
    private static unsafe UInt256 HashPair(in UInt256 left, in UInt256 right)
    {
        ulong* state = stackalloc ulong[12];
        state[0] = Sha256Init[0];
        state[1] = Sha256Init[1];
        state[2] = Sha256Init[2];
        state[3] = Sha256Init[3];

        ulong* input = state + 4;
        Unsafe.WriteUnaligned(input, left);
        Unsafe.WriteUnaligned(input + 4, right);

        void** parameters = stackalloc void*[2];
        parameters[0] = state;
        parameters[1] = input;
        syscall_sha256_f(parameters);

        fixed (ulong* pad = Sha256PadFor64)
        {
            parameters[1] = pad;
            syscall_sha256_f(parameters);
        }

        return DigestToUInt256(state);
    }

    /// <summary>Hashes a whole-chunk message with SHA-256 via the compression precompile.</summary>
    /// <remarks>Chunked input means the tail is always a single padding block: 32 remaining bytes
    /// plus terminator and length still fit (41 &lt;= 64).</remarks>
    [SkipLocalsInit]
    private static unsafe UInt256 Compute(Span<UInt256> span)
    {
        ulong* state = stackalloc ulong[4];
        state[0] = Sha256Init[0];
        state[1] = Sha256Init[1];
        state[2] = Sha256Init[2];
        state[3] = Sha256Init[3];

        void** parameters = stackalloc void*[2];
        parameters[0] = state;

        fixed (UInt256* data = span)
        {
            ulong* block = (ulong*)data;
            for (int i = span.Length >> 1; i > 0; i--)
            {
                parameters[1] = block;
                syscall_sha256_f(parameters);
                block += 8;
            }

            ulong* last = stackalloc ulong[8];
            if ((span.Length & 1) != 0)
            {
                last[0] = block[0];
                last[1] = block[1];
                last[2] = block[2];
                last[3] = block[3];
                last[4] = 0x80;
                last[5] = 0;
            }
            else
            {
                last[0] = 0x80;
                last[1] = 0;
                last[2] = 0;
                last[3] = 0;
                last[4] = 0;
                last[5] = 0;
            }
            last[6] = 0;
            // Message bit length, big-endian in the trailing eight bytes.
            ulong bitLength = Bswap32Pairs((ulong)span.Length << 8);
            last[7] = (bitLength << 32) | (bitLength >> 32);

            parameters[1] = last;
            syscall_sha256_f(parameters);
        }

        return DigestToUInt256(state);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe UInt256 DigestToUInt256(ulong* state)
    {
        // The digest is the eight state words big-endian; swap each 32-bit half in place.
        UInt256 result;
        ulong* r = (ulong*)&result;
        r[0] = Bswap32Pairs(state[0]);
        r[1] = Bswap32Pairs(state[1]);
        r[2] = Bswap32Pairs(state[2]);
        r[3] = Bswap32Pairs(state[3]);
        return result;
    }

    // Frozen-array loads rather than literals: the riscv64 backend materializes each 64-bit constant
    // with a five-instruction sequence at every inlined use.
    private static readonly ulong[] SwapMasks = [0x00FF00FF00FF00FFUL, 0x0000FFFF0000FFFFUL];

    /// <summary>Byte-swaps each 32-bit half of <paramref name="x"/> without crossing the halves.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Bswap32Pairs(ulong x)
    {
        ref ulong masks = ref MemoryMarshal.GetArrayDataReference(SwapMasks);
        ulong m8 = masks;
        ulong m16 = Unsafe.Add(ref masks, 1);
        x = ((x & m8) << 8) | ((x >> 8) & m8);
        return ((x & m16) << 16) | ((x >> 16) & m16);
    }
}
