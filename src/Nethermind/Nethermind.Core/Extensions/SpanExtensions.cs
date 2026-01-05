// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Core.Extensions
{
    public static class SpanExtensions
    {
        // Ensure that hashes are different for every run of the node and every node, so if are any hash collisions on
        // one node they will not be the same on another node or across a restart so hash collision cannot be used to degrade
        // the performance of the network as a whole.
        private static readonly uint s_instanceRandom = (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue);

        public static string ToHexString(this in Memory<byte> memory, bool withZeroX = false)
        {
            return ToHexString(memory.Span, withZeroX, false, false);
        }

        public static string ToHexString(this in ReadOnlyMemory<byte> memory, bool withZeroX = false)
        {
            return ToHexString(memory.Span, withZeroX, false, false);
        }

        public static string ToHexString(this in ReadOnlySpan<byte> span, bool withZeroX)
        {
            return ToHexString(span, withZeroX, false, false);
        }

        public static string ToHexString(this in Span<byte> span, bool withZeroX)
        {
            return ToHexViaLookup(span, withZeroX, false, false);
        }

        public static string ToHexString(this in ReadOnlySpan<byte> span, bool withZeroX, bool noLeadingZeros)
        {
            return ToHexViaLookup(span, withZeroX, noLeadingZeros, false);
        }

        public static string ToHexString(this in ReadOnlySpan<byte> span)
        {
            return ToHexString(span, false, false, false);
        }

        public static string ToHexString(this in Span<byte> span)
        {
            return ToHexViaLookup(span, false, false, false);
        }

        public static string ToHexString(this in ReadOnlySpan<byte> span, bool withZeroX, bool noLeadingZeros, bool withEip55Checksum)
        {
            return ToHexViaLookup(span, withZeroX, noLeadingZeros, withEip55Checksum);
        }

        public static string ToHexString(this in Span<byte> span, bool withZeroX, bool noLeadingZeros, bool withEip55Checksum)
        {
            return ToHexViaLookup(span, withZeroX, noLeadingZeros, withEip55Checksum);
        }

        [DebuggerStepThrough]
        private static unsafe string ToHexViaLookup(ReadOnlySpan<byte> bytes, bool withZeroX, bool skipLeadingZeros, bool withEip55Checksum)
        {
            if (withEip55Checksum)
            {
                return ToHexStringWithEip55Checksum(bytes, withZeroX, skipLeadingZeros);
            }
            if (bytes.Length == 0) return "";

            int leadingZeros = skipLeadingZeros ? bytes.CountLeadingNibbleZeros() : 0;
            int length = bytes.Length * 2 + (withZeroX ? 2 : 0) - leadingZeros;

            if (skipLeadingZeros && length == (withZeroX ? 2 : 0))
            {
                return withZeroX ? Bytes.ZeroHexValue : Bytes.ZeroValue;
            }

            fixed (byte* input = &Unsafe.Add(ref MemoryMarshal.GetReference(bytes), leadingZeros / 2))
            {
                var createParams = new StringParams(input, bytes.Length, leadingZeros, withZeroX);
                return string.Create(length, createParams, static (chars, state) =>
                {

                    Bytes.OutputBytesToCharHex(ref state.Input, state.InputLength, ref MemoryMarshal.GetReference(chars), state.WithZeroX, state.LeadingZeros);
                });
            }
        }

        readonly unsafe struct StringParams(byte* input, int inputLength, int leadingZeros, bool withZeroX)
        {
            private readonly byte* _input = input;
            public readonly int InputLength = inputLength;
            public readonly int LeadingZeros = leadingZeros;
            public readonly bool WithZeroX = withZeroX;

            public readonly ref byte Input => ref Unsafe.AsRef<byte>(_input);
        }

        private static string ToHexStringWithEip55Checksum(ReadOnlySpan<byte> bytes, bool withZeroX, bool skipLeadingZeros)
        {
            string hashHex = Keccak.Compute(bytes.ToHexString(false)).ToString(false);

            int leadingZeros = skipLeadingZeros ? bytes.CountLeadingNibbleZeros() : 0;
            int length = bytes.Length * 2 + (withZeroX ? 2 : 0) - leadingZeros;
            if (leadingZeros >= 2)
            {
                bytes = bytes[(leadingZeros / 2)..];
            }
            char[] charArray = ArrayPool<char>.Shared.Rent(length);

            Span<char> chars = charArray.AsSpan(0, length);

            if (withZeroX)
            {
                // In reverse, bounds check on [1] will elide bounds check on [0]
                chars[1] = 'x';
                chars[0] = '0';
                // Trim off the two chars from the span
                chars = chars[2..];
            }

            uint[] lookup32 = Bytes.Lookup32;

            bool odd = (leadingZeros & 1) != 0;
            int oddity = odd ? 2 : 0;
            if (odd)
            {
                // Odd number of hex chars, handle the first
                // separately so loop can work in pairs
                uint val = lookup32[bytes[0]];
                char char2 = (char)(val >> 16);
                chars[0] = char.IsLetter(char2) && hashHex[1] > '7'
                            ? char.ToUpper(char2)
                            : char2;

                // Trim off the first byte and char from the spans
                chars = chars[1..];
                bytes = bytes[1..];
            }

            for (int i = 0; i < chars.Length; i += 2)
            {
                uint val = lookup32[bytes[i / 2]];
                char char1 = (char)val;
                char char2 = (char)(val >> 16);

                chars[i] = char.IsLetter(char1) && hashHex[i + oddity] > '7'
                            ? char.ToUpper(char1)
                            : char1;
                chars[i + 1] = char.IsLetter(char2) && hashHex[i + 1 + oddity] > '7'
                            ? char.ToUpper(char2)
                            : char2;
            }

            string result = new string(charArray.AsSpan(0, length));
            ArrayPool<char>.Shared.Return(charArray);

            return result;
        }

        public static ReadOnlySpan<T> TakeAndMove<T>(this ref ReadOnlySpan<T> span, int length)
        {
            ReadOnlySpan<T> s = span[..length];
            span = span[length..];
            return s;
        }

        public static Span<T> TakeAndMove<T>(this ref Span<T> span, int length)
        {
            Span<T> s = span[..length];
            span = span[length..];
            return s;
        }

        public static bool IsNullOrEmpty<T>(this in Span<T> span) => span.Length == 0;
        public static bool IsNull<T>(this in Span<T> span) => Unsafe.IsNullRef(ref MemoryMarshal.GetReference(span));
        public static bool IsNullOrEmpty<T>(this in ReadOnlySpan<T> span) => span.Length == 0;
        public static bool IsNull<T>(this in ReadOnlySpan<T> span) => Unsafe.IsNullRef(ref MemoryMarshal.GetReference(span));

        public static ArrayPoolList<T> ToPooledList<T>(this in ReadOnlySpan<T> span)
        {
            ArrayPoolList<T> newList = new(span.Length);
            newList.AddRange(span);
            return newList;
        }

        public static ArrayPoolListRef<T> ToPooledListRef<T>(this in ReadOnlySpan<T> span)
        {
            ArrayPoolListRef<T> newList = new(span.Length);
            newList.AddRange(span);
            return newList;
        }

        /// <summary>
        /// Computes a very fast, non-cryptographic 32-bit hash of the supplied bytes.
        /// </summary>
        /// <param name="input">The input bytes to hash.</param>
        /// <returns>
        /// A 32-bit hash value for <paramref name="input"/>. Returns 0 when <paramref name="input"/> is empty.
        /// Note that the value is returned as a signed <see cref="int"/> (the underlying 32-bit pattern may appear negative).
        /// </returns>
        /// <remarks>
        /// <para>
        /// This routine is optimised for throughput and low overhead on modern CPUs. It is based on CRC32C (Castagnoli)
        /// via <see cref="BitOperations.Crc32C(uint, ulong)"/> and related overloads, and will use hardware acceleration
        /// when the runtime and processor support it.
        /// </para>
        /// <para>
        /// The hash is intended for in-memory data structures (for example, hash tables, caches, and quick bucketing).
        /// It is not suitable for cryptographic purposes, integrity verification, or security-sensitive scenarios.
        /// In particular, it is not collision resistant and should not be used as a MAC, signature, or authentication token.
        /// </para>
        /// <para>
        /// The returned value is an implementation detail. It is seeded with an instance-random value and may be
        /// platform and runtime dependent, so do not persist it or rely on it being stable across processes or versions.
        /// </para>
        /// </remarks>
        [SkipLocalsInit]
        public static int FastHash(this ReadOnlySpan<byte> input)
        {
            // Fast hardware-accelerated, non-cryptographic hash.
            // Core idea: CRC32C is extremely cheap on CPUs with SSE4.2/ARM CRC,
            // and gives good diffusion for hashing. We then optionally add extra
            // mixing to reduce "CRC linearity" artefacts.

            int len = input.Length;

            // Contract choice: empty input hashes to 0.
            // (Also avoids doing any ref work on an empty span.)
            if (len == 0) return 0;
            // Using ref + Unsafe.ReadUnaligned lets the JIT hoist bounds checks
            // and keep the hot loop tight.
            ref byte start = ref MemoryMarshal.GetReference(input);

            // Seed with an instance-random value so attackers cannot trivially
            // engineer lots of same-bucket keys. Mixing in length makes "same prefix,
            // different length" less correlated (CRC alone can be length-sensitive).
            uint seed = s_instanceRandom + (uint)len;

            // Small: 1-7 bytes.
            // Using the tail routine here avoids building a synthetic
            // 64-bit value with shifts/byte-permute.
            if (len < 8)
            {
                uint small = CrcTailOrdered(seed, ref start, len);
                // FinalMix breaks some remaining linearity and improves avalanche for tiny inputs.
                return (int)FinalMix(small);
            }

            // Medium: 8-31 bytes.
            // A single CRC lane is usually fine here - overhead dominates,
            // and latency hiding is less important.
            if (len < 32)
            {
                uint h = seed;
                ref byte p = ref start;

                // Process as many full qwords as possible.
                // "& ~7" is a cheap round-down-to-multiple-of-8 (no division/mod).
                int full = len & ~7;
                int tail = len - full;

                // Streaming CRC over 8-byte chunks.
                // ReadUnaligned keeps us safe for arbitrary input alignment.
                for (int i = 0; i < full; i += 8)
                {
                    h = BitOperations.Crc32C(h, Unsafe.ReadUnaligned<ulong>(ref p));
                    p = ref Unsafe.Add(ref p, 8);
                }

                // Hash remaining 1-7 bytes in strict order (no over-read).
                if (tail != 0)
                    h = CrcTailOrdered(h, ref p, tail);

                // Final mixing for better bit diffusion than raw CRC,
                // especially for shorter payloads.
                return (int)FinalMix(h);
            }

            // Large: 32+ bytes.
            // Use multiple independent CRC accumulators ("lanes") to hide crc32
            // latency and increase ILP. CRC32C instructions have decent throughput
            // but non-trivial latency; 4 lanes keeps the CPU busy.
            uint h0 = seed;
            uint h1 = seed ^ 0x9E3779B9u; // golden-ratio-ish constants to decorrelate lanes
            uint h2 = seed ^ 0x85EBCA6Bu; // constants borrowed from common finalisers (good bit dispersion)
            uint h3 = seed ^ 0xC2B2AE35u;

            ref byte q = ref start;

            // Consume all full qwords first. Tail (1-7 bytes) is handled later.
            int aligned = len & ~7;
            int remaining = aligned;

            // 64-byte unroll:
            // - amortises loop branch/compare overhead
            // - feeds enough independent work to keep OoO cores busy
            // - maps nicely onto cache line sized chunks
            while (remaining >= 64)
            {
                h0 = BitOperations.Crc32C(h0, Unsafe.ReadUnaligned<ulong>(ref q));
                h1 = BitOperations.Crc32C(h1, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 8)));
                h2 = BitOperations.Crc32C(h2, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 16)));
                h3 = BitOperations.Crc32C(h3, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 24)));

                h0 = BitOperations.Crc32C(h0, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 32)));
                h1 = BitOperations.Crc32C(h1, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 40)));
                h2 = BitOperations.Crc32C(h2, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 48)));
                h3 = BitOperations.Crc32C(h3, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 56)));

                q = ref Unsafe.Add(ref q, 64);
                remaining -= 64;
            }

            // One more half-unroll for 32 bytes if present.
            // Keeps the "drain" path short and avoids a smaller loop with more branches.
            if (remaining >= 32)
            {
                h0 = BitOperations.Crc32C(h0, Unsafe.ReadUnaligned<ulong>(ref q));
                h1 = BitOperations.Crc32C(h1, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 8)));
                h2 = BitOperations.Crc32C(h2, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 16)));
                h3 = BitOperations.Crc32C(h3, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 24)));

                q = ref Unsafe.Add(ref q, 32);
                remaining -= 32;
            }

            // Drain any remaining full qwords (0, 8, 16, or 24 bytes).
            // This is branchy but only runs once, so it is cheaper than another loop.
            if (remaining != 0)
            {
                // remaining is a multiple of 8 here.
                if (remaining >= 8) h0 = BitOperations.Crc32C(h0, Unsafe.ReadUnaligned<ulong>(ref q));
                if (remaining >= 16) h1 = BitOperations.Crc32C(h1, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 8)));
                if (remaining == 24) h2 = BitOperations.Crc32C(h2, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 16)));
            }

            // Fold lanes down to one 32-bit value.
            // Rotates permute bit positions so each lane contributes differently.
            // Adds (rather than XOR) deliberately introduce carries
            // - CRC is linear over GF(2), and carry breaks that, making simple algebraic
            // structure harder to exploit for collision clustering in hash tables.
            h2 = BitOperations.RotateLeft(h2, 17) + BitOperations.RotateLeft(h3, 23);
            h0 += BitOperations.RotateLeft(h1, 11);
            uint hash = h2 + h0;

            // Handle tail bytes (1-7 bytes) that were not part of the qword-aligned stream.
            // This is exact, in-order processing - no overlap and no over-read.
            int tailBytes = len - aligned;
            if (tailBytes != 0)
            {
                ref byte tailRef = ref Unsafe.Add(ref start, aligned);
                hash = CrcTailOrdered(hash, ref tailRef, tailBytes);
            }

            // FinalMix breaks some remaining linearity and improves avalanche
            return (int)FinalMix(hash);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static uint CrcTailOrdered(uint hash, ref byte p, int length)
            {
                // length is 1..7
                // Process 4-2-1 bytes in natural order
                if ((length & 4) != 0)
                {
                    hash = BitOperations.Crc32C(hash, Unsafe.ReadUnaligned<uint>(ref p));
                    p = ref Unsafe.Add(ref p, 4);
                }
                if ((length & 2) != 0)
                {
                    hash = BitOperations.Crc32C(hash, Unsafe.ReadUnaligned<ushort>(ref p));
                    p = ref Unsafe.Add(ref p, 2);
                }
                if ((length & 1) != 0)
                {
                    hash = BitOperations.Crc32C(hash, p);
                }
                return hash;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static uint FinalMix(uint x)
            {
                // A tiny finaliser to improve avalanche:
                // - xor-fold high bits down
                // - multiply by an odd constant to spread changes across bits
                // - xor-fold again to propagate the multiply result
                x ^= x >> 16;
                x *= 0x9E3779B1u;
                x ^= x >> 16;
                return x;
            }
        }
    }
}
