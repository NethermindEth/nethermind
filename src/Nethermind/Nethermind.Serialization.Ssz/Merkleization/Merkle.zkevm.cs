// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz.Merkleization;

public static partial class Merkle
{
    // SHA-256 initial hash values (FIPS 180-4), packed as the [u64;4] layout syscall_sha256_f expects.
    private const ulong Sha256Init0 = 0x6a09e667 | ((ulong)0xbb67ae85 << 32);
    private const ulong Sha256Init1 = 0x3c6ef372 | ((ulong)0xa54ff53a << 32);
    private const ulong Sha256Init2 = 0x510e527f | ((ulong)0x9b05688c << 32);
    private const ulong Sha256Init3 = 0x1f83d9ab | ((ulong)0x5be0cd19 << 32);

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
        state[0] = Sha256Init0;
        state[1] = Sha256Init1;
        state[2] = Sha256Init2;
        state[3] = Sha256Init3;

        ulong* input = state + 4;
        Unsafe.WriteUnaligned(input, left);
        Unsafe.WriteUnaligned(input + 4, right);

        void** parameters = stackalloc void*[2];
        parameters[0] = state;
        parameters[1] = input;
        syscall_sha256_f(parameters);

        // The second compression block of any 64-byte message is constant: 0x80 terminator,
        // zeros, and the 512-bit message length in the trailing eight big-endian bytes.
        input[0] = 0x80;
        input[1] = 0;
        input[2] = 0;
        input[3] = 0;
        input[4] = 0;
        input[5] = 0;
        input[6] = 0;
        input[7] = 0x0002_0000_0000_0000;
        syscall_sha256_f(parameters);

        // The digest is the eight state words big-endian; swap each 32-bit half in place.
        UInt256 result;
        ulong* r = (ulong*)&result;
        r[0] = Bswap32Pairs(state[0]);
        r[1] = Bswap32Pairs(state[1]);
        r[2] = Bswap32Pairs(state[2]);
        r[3] = Bswap32Pairs(state[3]);
        return result;
    }

    /// <summary>Byte-swaps each 32-bit half of <paramref name="x"/> without crossing the halves.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Bswap32Pairs(ulong x)
    {
        x = ((x & 0x00FF00FF00FF00FFUL) << 8) | ((x >> 8) & 0x00FF00FF00FF00FFUL);
        return ((x & 0x0000FFFF0000FFFFUL) << 16) | ((x >> 16) & 0x0000FFFF0000FFFFUL);
    }
}
