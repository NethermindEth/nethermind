// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Arm = System.Runtime.Intrinsics.Arm;
using x64 = System.Runtime.Intrinsics.X86;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Core.Extensions
{
    public static class SpanExtensions
    {
        // Ensure that hashes are different for every run of the node and every node, so if are any hash collisions on
        // one node, they will not be the same on another node or across a restart so hash collision cannot be used to degrade
        // the performance of the network as a whole.
        public static readonly uint InstanceRandom =
#if ZKVM
            2098026241U;
#else
            (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue);
#endif
        internal static uint ComputeSeed(int len) => InstanceRandom + (uint)len;

        /// <summary>
        /// Computes a very fast, non-cryptographic 32-bit hash of exactly 32 bytes.
        /// </summary>
        /// <param name="start">Reference to the first byte of the 32-byte input.</param>
        /// <returns>A 32-bit hash value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int FastHash32(ref byte start)
        {
            Debug.Assert(x64.Aes.IsSupported || Arm.Aes.IsSupported, "FastHash32 requires AES hardware support");

            uint seed = InstanceRandom + 32;
            Vector128<byte> key = Unsafe.As<byte, Vector128<byte>>(ref start);
            Vector128<byte> data = Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref start, 16));
            key ^= Vector128.CreateScalar(seed).AsByte();
            Vector128<byte> mixed = x64.Aes.IsSupported
                ? x64.Aes.Encrypt(data, key)
                : Arm.Aes.MixColumns(Arm.Aes.Encrypt(data, key));
            ulong compressed = mixed.AsUInt64().GetElement(0) ^ mixed.AsUInt64().GetElement(1);
            return (int)(uint)(compressed ^ (compressed >> 32));
        }

        public static string ToHexString(this in ReadOnlyMemory<byte> memory, bool withZeroX = false) =>
            memory.Span.ToHexString(withZeroX, false, false);

        extension(in ReadOnlySpan<byte> span)
        {
            public string ToHexString(bool withZeroX) =>
                span.ToHexString(withZeroX, false, false);

            public string ToHexString(bool withZeroX, bool noLeadingZeros) =>
                ToHexViaLookup(span, withZeroX, noLeadingZeros, false);

            public string ToHexString() =>
                span.ToHexString(false, false, false);

            public string ToHexString(bool withZeroX, bool noLeadingZeros, bool withEip55Checksum) =>
                ToHexViaLookup(span, withZeroX, noLeadingZeros, withEip55Checksum);
        }

        extension(in Span<byte> span)
        {
            public string ToHexString(bool withZeroX) =>
                ToHexViaLookup(span, withZeroX, false, false);

            public string ToHexString() =>
                ToHexViaLookup(span, false, false, false);

            public string ToHexString(bool withZeroX, bool noLeadingZeros, bool withEip55Checksum) =>
                ToHexViaLookup(span, withZeroX, noLeadingZeros, withEip55Checksum);
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
        /// This routine is optimized for throughput and low overhead on modern CPUs. It is based on CRC32C (Castagnoli)
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
            => FastHash(input, InstanceRandom + (uint)input.Length);

        internal static int FastHash(ReadOnlySpan<byte> input, uint seed)
        {
            int len = input.Length;
            if (len == 0) return 0;

            ref byte start = ref MemoryMarshal.GetReference(input);

            if (len >= 16)
            {
                if (x64.Aes.IsSupported) return FastHashAesX64(ref start, len, seed);
                if (Arm.Aes.IsSupported) return FastHashAesArm(ref start, len, seed);
            }

            return FastHashCrc(ref start, len, seed);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SkipLocalsInit]
        internal static int FastHashAesX64(ref byte start, int len, uint seed)
        {
            Vector128<byte> seedVec = Vector128.CreateScalar(seed).AsByte();
            Vector128<byte> acc0 = Unsafe.As<byte, Vector128<byte>>(ref start) ^ seedVec;

            if (len > 64)
            {
                Vector128<byte> acc1 = Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref start, 16)) ^ seedVec;
                Vector128<byte> acc2 = Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref start, 32)) ^ seedVec;
                Vector128<byte> acc3 = Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref start, 48)) ^ seedVec;

                ref byte p = ref Unsafe.Add(ref start, 64);
                int remaining = len - 64;

                while (remaining >= 64)
                {
                    acc0 = x64.Aes.Encrypt(Unsafe.As<byte, Vector128<byte>>(ref p), acc0);
                    acc1 = x64.Aes.Encrypt(Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref p, 16)), acc1);
                    acc2 = x64.Aes.Encrypt(Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref p, 32)), acc2);
                    acc3 = x64.Aes.Encrypt(Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref p, 48)), acc3);

                    p = ref Unsafe.Add(ref p, 64);
                    remaining -= 64;
                }

                // Fold 4 lanes: 3 XOR + 1 AES (minimal serial latency)
                acc0 ^= acc1;
                acc2 ^= acc3;
                acc0 ^= acc2;
                acc0 = x64.Aes.Encrypt(seedVec, acc0);

                // Drain remaining 0-63 bytes
                while (remaining >= 16)
                {
                    acc0 = x64.Aes.Encrypt(Unsafe.As<byte, Vector128<byte>>(ref p), acc0);
                    p = ref Unsafe.Add(ref p, 16);
                    remaining -= 16;
                }

                // Remaining 1-15 bytes: use CRC to avoid overlap with drain blocks
                if (remaining > 0)
                {
                    uint crc = seed;
                    if (remaining >= 8)
                    {
                        crc = BitOperations.Crc32C(crc, Unsafe.ReadUnaligned<ulong>(ref p));
                        p = ref Unsafe.Add(ref p, 8);
                        remaining -= 8;
                    }
                    if ((remaining & 4) != 0)
                    {
                        crc = BitOperations.Crc32C(crc, Unsafe.ReadUnaligned<uint>(ref p));
                        p = ref Unsafe.Add(ref p, 4);
                    }
                    if ((remaining & 2) != 0)
                    {
                        crc = BitOperations.Crc32C(crc, Unsafe.ReadUnaligned<ushort>(ref p));
                        p = ref Unsafe.Add(ref p, 2);
                    }
                    if ((remaining & 1) != 0)
                    {
                        crc = BitOperations.Crc32C(crc, p);
                    }
                    acc0 = x64.Aes.Encrypt(Vector128.CreateScalar(crc).AsByte(), acc0);
                }
            }
            else if (len > 32)
            {
                ref byte p = ref Unsafe.Add(ref start, 16);
                int remaining = len - 16;

                while (remaining > 16)
                {
                    acc0 = x64.Aes.Encrypt(Unsafe.As<byte, Vector128<byte>>(ref p), acc0);
                    p = ref Unsafe.Add(ref p, 16);
                    remaining -= 16;
                }

                Vector128<byte> last = Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref start, len - 16));
                acc0 = x64.Aes.Encrypt(last, acc0);
            }
            else
            {
                Vector128<byte> data = Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref start, len - 16));
                acc0 = x64.Aes.Encrypt(data, acc0);
            }

            ulong compressed = acc0.AsUInt64().GetElement(0) ^ acc0.AsUInt64().GetElement(1);
            return (int)(uint)(compressed ^ (compressed >> 32));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SkipLocalsInit]
        internal static int FastHashAesArm(ref byte start, int len, uint seed)
        {
            Vector128<byte> seedVec = Vector128.CreateScalar(seed).AsByte();
            Vector128<byte> input0 = Unsafe.As<byte, Vector128<byte>>(ref start);
            Vector128<byte> acc0 = input0 ^ seedVec;

            if (len > 64)
            {
                Vector128<byte> acc1 = Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref start, 16)) ^ seedVec;
                Vector128<byte> acc2 = Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref start, 32)) ^ seedVec;
                Vector128<byte> acc3 = Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref start, 48)) ^ seedVec;

                ref byte p = ref Unsafe.Add(ref start, 64);
                int remaining = len - 64;

                while (remaining >= 64)
                {
                    acc0 = Arm.Aes.MixColumns(Arm.Aes.Encrypt(Unsafe.As<byte, Vector128<byte>>(ref p), acc0));
                    acc1 = Arm.Aes.MixColumns(Arm.Aes.Encrypt(Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref p, 16)), acc1));
                    acc2 = Arm.Aes.MixColumns(Arm.Aes.Encrypt(Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref p, 32)), acc2));
                    acc3 = Arm.Aes.MixColumns(Arm.Aes.Encrypt(Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref p, 48)), acc3));

                    p = ref Unsafe.Add(ref p, 64);
                    remaining -= 64;
                }

                acc0 ^= acc1;
                acc2 ^= acc3;
                acc0 ^= acc2;
                acc0 = Arm.Aes.MixColumns(Arm.Aes.Encrypt(seedVec, acc0));

                while (remaining >= 16)
                {
                    acc0 = Arm.Aes.MixColumns(Arm.Aes.Encrypt(Unsafe.As<byte, Vector128<byte>>(ref p), acc0));
                    p = ref Unsafe.Add(ref p, 16);
                    remaining -= 16;
                }

                if (remaining > 0)
                {
                    uint crc = seed;
                    if (remaining >= 8)
                    {
                        crc = BitOperations.Crc32C(crc, Unsafe.ReadUnaligned<ulong>(ref p));
                        p = ref Unsafe.Add(ref p, 8);
                        remaining -= 8;
                    }
                    if ((remaining & 4) != 0)
                    {
                        crc = BitOperations.Crc32C(crc, Unsafe.ReadUnaligned<uint>(ref p));
                        p = ref Unsafe.Add(ref p, 4);
                    }
                    if ((remaining & 2) != 0)
                    {
                        crc = BitOperations.Crc32C(crc, Unsafe.ReadUnaligned<ushort>(ref p));
                        p = ref Unsafe.Add(ref p, 2);
                    }
                    if ((remaining & 1) != 0)
                    {
                        crc = BitOperations.Crc32C(crc, p);
                    }
                    acc0 = Arm.Aes.MixColumns(Arm.Aes.Encrypt(Vector128.CreateScalar(crc).AsByte(), acc0));
                }
            }
            else if (len > 32)
            {
                ref byte p = ref Unsafe.Add(ref start, 16);
                int remaining = len - 16;

                while (remaining > 16)
                {
                    acc0 = Arm.Aes.MixColumns(Arm.Aes.Encrypt(Unsafe.As<byte, Vector128<byte>>(ref p), acc0));
                    p = ref Unsafe.Add(ref p, 16);
                    remaining -= 16;
                }

                Vector128<byte> last = Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref start, len - 16));
                acc0 = Arm.Aes.MixColumns(Arm.Aes.Encrypt(last, acc0));
            }
            else if (len > 16)
            {
                Vector128<byte> data = Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref start, len - 16));
                acc0 = Arm.Aes.MixColumns(Arm.Aes.Encrypt(data, acc0));
            }
            else
            {
                // len == 16: start+len-16 == start, so data would be the same bytes
                // that built acc0. ARM Arm.Aes.Encrypt XORs its operands before scrambling,
                // so Encrypt(input, input^seed) cancels input, losing all input dependence.
                // Feed input and seedVec directly so the XOR yields (input ^ seed),
                // then SubBytes and ShiftRows, and Arm.Aes.MixColumns completes the round.
                acc0 = Arm.Aes.MixColumns(Arm.Aes.Encrypt(input0, seedVec));
            }

            ulong compressed = acc0.AsUInt64().GetElement(0) ^ acc0.AsUInt64().GetElement(1);
            return (int)(uint)(compressed ^ (compressed >> 32));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SkipLocalsInit]
        internal static int FastHashCrc(ref byte start, int len, uint seed)
        {
            uint hash;
            if (len < 16)
            {
                if (len >= 8)
                {
                    ulong lo = Unsafe.ReadUnaligned<ulong>(ref start);
                    ulong hi = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, len - 8));
                    uint h0 = BitOperations.Crc32C(seed, lo);
                    uint h1 = BitOperations.Crc32C(seed ^ 0x9E3779B9u, hi);
                    hash = h0 + BitOperations.RotateLeft(h1, 11);
                }
                else
                {
                    hash = CrcTailOrdered(seed, ref start, len);
                }
            }
            else
            {
                uint h0 = seed;
                uint h1 = seed ^ 0x9E3779B9u;
                uint h2 = seed ^ 0x85EBCA6Bu;
                uint h3 = seed ^ 0xC2B2AE35u;

                ref byte q = ref start;
                int aligned = len & ~7;
                int remaining = aligned;

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

                if (remaining >= 32)
                {
                    h0 = BitOperations.Crc32C(h0, Unsafe.ReadUnaligned<ulong>(ref q));
                    h1 = BitOperations.Crc32C(h1, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 8)));
                    h2 = BitOperations.Crc32C(h2, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 16)));
                    h3 = BitOperations.Crc32C(h3, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 24)));

                    q = ref Unsafe.Add(ref q, 32);
                    remaining -= 32;
                }

                if (remaining >= 8) h0 = BitOperations.Crc32C(h0, Unsafe.ReadUnaligned<ulong>(ref q));
                if (remaining >= 16) h1 = BitOperations.Crc32C(h1, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 8)));
                if (remaining >= 24) h2 = BitOperations.Crc32C(h2, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 16)));

                h2 = BitOperations.RotateLeft(h2, 17) + BitOperations.RotateLeft(h3, 23);
                h0 += BitOperations.RotateLeft(h1, 11);
                hash = h2 + h0;

                int tailBytes = len - aligned;
                if (tailBytes != 0)
                {
                    ref byte tailRef = ref Unsafe.Add(ref start, aligned);
                    hash = CrcTailOrdered(hash, ref tailRef, tailBytes);
                }
            }

            hash ^= hash >> 16;
            hash *= 0x9E3779B1u;
            hash ^= hash >> 16;
            return (int)hash;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static uint CrcTailOrdered(uint hash, ref byte p, int length)
            {
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
        }

        /// <summary>
        /// Computes a very fast, non-cryptographic 64-bit hash of exactly 32 bytes.
        /// </summary>
        /// <param name="start">Reference to the first byte of the 32-byte input.</param>
        /// <returns>A 64-bit hash value with good distribution across all bits.</returns>
        /// <remarks>
        /// Uses AES hardware acceleration when available, falls back to CRC32C otherwise.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long FastHash64For32Bytes(ref byte start)
        {
            uint seed = InstanceRandom + 32;

            if (x64.Aes.IsSupported || Arm.Aes.IsSupported)
            {
                Vector128<byte> key = Unsafe.As<byte, Vector128<byte>>(ref start);
                Vector128<byte> data = Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref start, 16));
                key ^= Vector128.CreateScalar(seed).AsByte();
                Vector128<byte> mixed = x64.Aes.IsSupported
                    ? x64.Aes.Encrypt(data, key)
                    : Arm.Aes.MixColumns(Arm.Aes.Encrypt(data, key));
                return (long)(mixed.AsUInt64().GetElement(0) ^ mixed.AsUInt64().GetElement(1));
            }

            // Fallback: CRC32C-based 64-bit hash
            ulong h0 = BitOperations.Crc32C(seed, Unsafe.ReadUnaligned<ulong>(ref start));
            ulong h1 = BitOperations.Crc32C(seed ^ 0x9E3779B9u, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, 8)));
            ulong h2 = BitOperations.Crc32C(seed ^ 0x85EBCA6Bu, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, 16)));
            ulong h3 = BitOperations.Crc32C(seed ^ 0xC2B2AE35u, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, 24)));
            return (long)((h0 | (h1 << 32)) ^ (h2 | (h3 << 32)));
        }

        /// <summary>
        /// Computes a very fast, non-cryptographic 64-bit hash of exactly 20 bytes (Address size).
        /// </summary>
        /// <param name="start">Reference to the first byte of the 20-byte input.</param>
        /// <returns>A 64-bit hash value with good distribution across all bits.</returns>
        /// <remarks>
        /// Uses AES hardware acceleration when available, falls back to CRC32C otherwise.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long FastHash64For20Bytes(ref byte start)
        {
            uint seed = InstanceRandom + 20;

            if (x64.Aes.IsSupported || Arm.Aes.IsSupported)
            {
                Vector128<byte> key = Unsafe.As<byte, Vector128<byte>>(ref start);
                uint last4 = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref start, 16));
                Vector128<byte> data = Vector128.CreateScalar(last4).AsByte();
                key ^= Vector128.CreateScalar(seed).AsByte();
                Vector128<byte> mixed = x64.Aes.IsSupported
                    ? x64.Aes.Encrypt(data, key)
                    : Arm.Aes.MixColumns(Arm.Aes.Encrypt(data, key));
                return (long)(mixed.AsUInt64().GetElement(0) ^ mixed.AsUInt64().GetElement(1));
            }

            // Fallback: CRC32C-based 64-bit hash
            ulong h0 = BitOperations.Crc32C(seed, Unsafe.ReadUnaligned<ulong>(ref start));
            ulong h1 = BitOperations.Crc32C(seed ^ 0x9E3779B9u, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, 8)));
            uint h2 = BitOperations.Crc32C(seed ^ 0x85EBCA6Bu, Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref start, 16)));
            return (long)((h0 | (h1 << 32)) ^ ((ulong)h2 * 0x9E3779B97F4A7C15));
        }
    }
}
