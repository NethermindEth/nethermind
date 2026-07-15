// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Arm = System.Runtime.Intrinsics.Arm;
using x64 = System.Runtime.Intrinsics.X86;
using Nethermind.Core.Collections;

namespace Nethermind.Core.Extensions
{
    public static partial class SpanExtensions
    {
        private const ulong ShortInputDomain = 0xD6E8FEB86659FD93UL;

        internal static uint ComputeSeed(int len) => InstanceRandom + (uint)len;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Vector128<byte> ComputeAesSeed(int len)
        {
            ulong lengthSalt = (uint)len;
            lengthSalt |= lengthSalt << 32;
            return Vector128.Create(AesHashSeed0 ^ lengthSalt, AesHashSeed1 ^ lengthSalt).AsByte();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Vector128<byte> ComputeAesFinalSeed()
            => Vector128.Create(AesHashFinalSeed0, AesHashFinalSeed1).AsByte();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> ComputeAes20Seed()
            => Vector128.Create(AesHash20Seed0, AesHash20Seed1).AsByte();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> ComputeAes32Seed()
            => Vector128.Create(AesHash32Seed0, AesHash32Seed1).AsByte();

        public static string ToHexString(this in Memory<byte> memory, bool withZeroX = false) =>
            memory.Span.ToHexString(withZeroX, false, false);

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
                StringParams createParams = new(input, bytes.Length, leadingZeros, withZeroX);
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
            int leadingZeros = skipLeadingZeros ? bytes.CountLeadingNibbleZeros() : 0;
            int length = bytes.Length * 2 + (withZeroX ? 2 : 0) - leadingZeros;
            if (skipLeadingZeros && length == (withZeroX ? 2 : 0))
            {
                return withZeroX ? Bytes.ZeroHexValue : Bytes.ZeroValue;
            }

            char[] charArray = ArrayPool<char>.Shared.Rent(length);

            Span<char> chars = charArray.AsSpan(0, length);
            try
            {
                bytes.OutputBytesToCharHexWithEip55Checksum(chars, withZeroX, leadingZeros);
                return new string(chars);
            }
            finally
            {
                ArrayPool<char>.Shared.Return(charArray);
            }
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
        /// Returns whether <paramref name="a"/>[aStart..aStart+length] sequence-equals
        /// <paramref name="b"/>[bStart..bStart+length]. Shorthand for the
        /// <c>a.Slice(aStart, length).SequenceEqual(b.Slice(bStart, length))</c> pattern.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool SliceEqual<T>(this ReadOnlySpan<T> a, int aStart, ReadOnlySpan<T> b, int bStart, int length) where T : IEquatable<T>
            => a.Slice(aStart, length).SequenceEqual(b.Slice(bStart, length));

        /// <summary>
        /// Copy <paramref name="src"/>[srcStart..srcStart+length] into
        /// <paramref name="dst"/>[dstStart..dstStart+length].
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CopySlice<T>(this Span<T> src, int srcStart, Span<T> dst, int dstStart, int length)
            => src.Slice(srcStart, length).CopyTo(dst.Slice(dstStart, length));

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
        /// This routine is optimized for throughput and low overhead on modern CPUs. It uses keyed AES rounds when
        /// hardware acceleration is available. Otherwise, normal builds use process-seeded XXH3 and ZK builds use
        /// their deterministic guest mixer.
        /// </para>
        /// <para>
        /// The hash is intended for in-memory data structures (for example, hash tables, caches, and quick bucketing).
        /// It is not suitable for cryptographic purposes or integrity verification.
        /// It must not be used as a MAC, signature, or authentication token.
        /// </para>
        /// <para>
        /// The returned value is an implementation detail. Normal builds use process-random seeds; ZK builds are
        /// deterministic. Do not persist it or rely on it being stable across platforms or versions.
        /// </para>
        /// </remarks>
        [SkipLocalsInit]
        public static int FastHash(this ReadOnlySpan<byte> input)
        {
            int len = input.Length;
            if (len == 0) return 0;

            ref byte start = ref MemoryMarshal.GetReference(input);
            if (x64.Aes.IsSupported || Arm.Aes.IsSupported)
            {
                return len < 16
                    ? FastHashAesShort(ref start, len, ComputeAesSeed(0), ComputeAesFinalSeed())
                    : FastHashAes(ref start, len, ComputeAesSeed(len));
            }

            return FastHashFallback(input);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> LoadShortInput(ref byte start, int len)
        {
            Debug.Assert((uint)(len - 1) < 15u);

            ulong lo;
            ulong hi;
            if (len >= 8)
            {
                lo = Unsafe.ReadUnaligned<ulong>(ref start);
                int remaining = len - sizeof(ulong);
                hi = ReadPartialWord(ref Unsafe.Add(ref start, sizeof(ulong)), remaining);
                hi |= 0x80UL << (remaining * 8);
            }
            else
            {
                lo = ReadPartialWord(ref start, len);
                lo |= 0x80UL << (len * 8);
                hi = 0;
            }

            return Vector128.Create(lo, hi ^ ShortInputDomain).AsByte();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static ulong ReadPartialWord(ref byte p, int length)
            {
                Debug.Assert((uint)length < sizeof(ulong));

                ulong value = 0;
                int offset = 0;
                if ((length & sizeof(uint)) != 0)
                {
                    value = Unsafe.ReadUnaligned<uint>(ref p);
                    offset = sizeof(uint);
                }
                if ((length & sizeof(ushort)) != 0)
                {
                    value |= (ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref p, offset)) << (offset * 8);
                    offset += sizeof(ushort);
                }
                if ((length & sizeof(byte)) != 0)
                    value |= (ulong)Unsafe.Add(ref p, offset) << (offset * 8);

                return value;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int FastHashAesShort(
            ref byte start,
            int len,
            Vector128<byte> seedVec,
            Vector128<byte> finalSeedVec)
        {
            Vector128<byte> mixed = FastHashAesRound(LoadShortInput(ref start, len), seedVec);
            mixed = FastHashAesRound(mixed, finalSeedVec);
            return (int)MumFold(mixed);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SkipLocalsInit]
        internal static int FastHashAes(ref byte start, int len, Vector128<byte> seedVec)
        {
            Vector128<byte> acc0 = FastHashAesRound(Unsafe.As<byte, Vector128<byte>>(ref start), seedVec);

            if (len > 64)
            {
                Vector128<byte> acc1 = FastHashAesRound(Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref start, 16)), seedVec);
                Vector128<byte> acc2 = FastHashAesRound(Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref start, 32)), seedVec);
                Vector128<byte> acc3 = FastHashAesRound(Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref start, 48)), seedVec);

                ref byte p = ref Unsafe.Add(ref start, 64);
                int remaining = len - 64;

                while (remaining >= 64)
                {
                    acc0 = FastHashAesRound(acc0, Unsafe.As<byte, Vector128<byte>>(ref p));
                    acc1 = FastHashAesRound(acc1, Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref p, 16)));
                    acc2 = FastHashAesRound(acc2, Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref p, 32)));
                    acc3 = FastHashAesRound(acc3, Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref p, 48)));

                    p = ref Unsafe.Add(ref p, 64);
                    remaining -= 64;
                }

                // Fold lanes with asymmetric AES mixing.
                Vector128<byte> m01 = FastHashAesRound(acc0, acc1);
                Vector128<byte> m23 = FastHashAesRound(acc2, acc3);
                acc0 = FastHashAesRound(m01, m23);

                // Drain remaining 0-63 bytes
                while (remaining >= 16)
                {
                    acc0 = FastHashAesRound(acc0, Unsafe.As<byte, Vector128<byte>>(ref p));
                    p = ref Unsafe.Add(ref p, 16);
                    remaining -= 16;
                }

                if (remaining > 0)
                {
                    Vector128<byte> last = Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref start, len - 16));
                    acc0 = FastHashAesRound(acc0, last);
                }
            }
            else if (len > 32)
            {
                ref byte p = ref Unsafe.Add(ref start, 16);
                int remaining = len - 16;

                while (remaining > 16)
                {
                    acc0 = FastHashAesRound(acc0, Unsafe.As<byte, Vector128<byte>>(ref p));
                    p = ref Unsafe.Add(ref p, 16);
                    remaining -= 16;
                }

                Vector128<byte> last = Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref start, len - 16));
                acc0 = FastHashAesRound(acc0, last);
            }
            else
            {
                Vector128<byte> data = Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref start, len - 16));
                acc0 = FastHashAesRound(acc0, data);
            }

            return (int)MumFold(acc0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> FastHashAesRound(Vector128<byte> state, Vector128<byte> roundKey)
            => x64.Aes.IsSupported
                ? x64.Aes.Encrypt(state, roundKey)
                // Keep the round key outside AESE so state and roundKey have distinct roles in the mixer.
                : Arm.Aes.MixColumns(Arm.Aes.Encrypt(state, Vector128<byte>.Zero)) ^ roundKey;

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
                    uint h0 = CrcLane(seed, lo);
                    uint h1 = CrcLane(seed ^ 0x9E3779B9u, hi);
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
                    h0 = CrcLane(h0, Unsafe.ReadUnaligned<ulong>(ref q));
                    h1 = CrcLane(h1, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 8)));
                    h2 = CrcLane(h2, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 16)));
                    h3 = CrcLane(h3, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 24)));

                    h0 = CrcLane(h0, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 32)));
                    h1 = CrcLane(h1, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 40)));
                    h2 = CrcLane(h2, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 48)));
                    h3 = CrcLane(h3, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 56)));

                    q = ref Unsafe.Add(ref q, 64);
                    remaining -= 64;
                }

                if (remaining >= 32)
                {
                    h0 = CrcLane(h0, Unsafe.ReadUnaligned<ulong>(ref q));
                    h1 = CrcLane(h1, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 8)));
                    h2 = CrcLane(h2, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 16)));
                    h3 = CrcLane(h3, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 24)));

                    q = ref Unsafe.Add(ref q, 32);
                    remaining -= 32;
                }

                if (remaining >= 8) h0 = CrcLane(h0, Unsafe.ReadUnaligned<ulong>(ref q));
                if (remaining >= 16) h1 = CrcLane(h1, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 8)));
                if (remaining >= 24) h2 = CrcLane(h2, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, 16)));

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
                    hash = Crc32C(hash, Unsafe.ReadUnaligned<uint>(ref p));
                    p = ref Unsafe.Add(ref p, 4);
                }
                if ((length & 2) != 0)
                {
                    hash = Crc32C(hash, Unsafe.ReadUnaligned<ushort>(ref p));
                    p = ref Unsafe.Add(ref p, 2);
                }
                if ((length & 1) != 0)
                {
                    hash = Crc32C(hash, p);
                }
                return hash;
            }
        }

        public static long ToPositiveLong(this ReadOnlySpan<byte> bytes)
        {
            return bytes.Length switch
            {
                0 => 0,
                // 1-7 bytes can never exceed long.MaxValue (they are at most 56 bits).
                < 8 => (long)ReadUInt64BigEndian1To7(bytes),
                // 8 bytes - only overflow if the top bit is set.
                8 => ReadInt64BigEndianChecked(bytes),
                _ => ParseLargeSpan(bytes),
            };

            static long ReadInt64BigEndianChecked(ReadOnlySpan<byte> bytes)
            {
                ulong value = BinaryPrimitives.ReadUInt64BigEndian(bytes);
                if (value > long.MaxValue)
                    ThrowExceedsMaxValue(bytes);

                return (long)value;
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            static long ParseLargeSpan(ReadOnlySpan<byte> bytes)
            {
                // length > 8:
                // Value fits in 64 bits iff the prefix (everything before the last 8 bytes) is all zeros.
                int prefixLen = bytes.Length - 8;

                // Vectorised in modern runtimes for byte spans.
                if (bytes.Slice(0, prefixLen).IndexOfAnyExcept((byte)0) >= 0)
                    ThrowExceedsMaxValue(bytes);

                ReadOnlySpan<byte> tail = bytes.Slice(prefixLen); // exactly 8 bytes

                ulong value = BinaryPrimitives.ReadUInt64BigEndian(tail);
                if (value > long.MaxValue)
                    ThrowExceedsMaxValue(bytes);

                return (long)value;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static ulong ReadUInt64BigEndian1To7(ReadOnlySpan<byte> s)
            {
                Debug.Assert((uint)s.Length - 1u < 7u);

                ref byte r0 = ref MemoryMarshal.GetReference(s);

                return s.Length switch
                {
                    1 => r0,

                    2 => ((ulong)r0 << 8)
                       | Unsafe.Add(ref r0, 1),

                    3 => ((ulong)r0 << 16)
                       | ((ulong)Unsafe.Add(ref r0, 1) << 8)
                       | Unsafe.Add(ref r0, 2),

                    4 => BinaryPrimitives.ReadUInt32BigEndian(s),

                    5 => ((ulong)BinaryPrimitives.ReadUInt32BigEndian(s) << 8)
                       | Unsafe.Add(ref r0, 4),

                    6 => ((ulong)BinaryPrimitives.ReadUInt32BigEndian(s) << 16)
                       | ((ulong)Unsafe.Add(ref r0, 4) << 8)
                       | Unsafe.Add(ref r0, 5),

                    7 => ((ulong)BinaryPrimitives.ReadUInt32BigEndian(s) << 24)
                       | ((ulong)Unsafe.Add(ref r0, 4) << 16)
                       | ((ulong)Unsafe.Add(ref r0, 5) << 8)
                       | Unsafe.Add(ref r0, 6),

                    _ => 0 // unreachable
                };
            }

            [DoesNotReturn, StackTraceHidden]
            static void ThrowExceedsMaxValue(ReadOnlySpan<byte> bytes)
            {
                BigInteger value = new(bytes, isUnsigned: true, isBigEndian: true);
                throw new OverflowException($"Value {value} exceeds maximum allowed value");
            }
        }

        /// <summary>
        /// Decodes a big-endian byte span (up to 8 bytes long) into an unsigned 64-bit integer.
        /// Inputs longer than 8 bytes are accepted only if all leading bytes are zero.
        /// </summary>
        public static ulong ToULong(this ReadOnlySpan<byte> bytes)
        {
            return bytes.Length switch
            {
                0 => 0UL,
                < 8 => ReadUInt64BigEndian1To7(bytes),
                8 => BinaryPrimitives.ReadUInt64BigEndian(bytes),
                _ => ParseLargeSpan(bytes),
            };

            static ulong ParseLargeSpan(ReadOnlySpan<byte> bytes)
            {
                int prefixLen = bytes.Length - 8;
                if (bytes.Slice(0, prefixLen).IndexOfAnyExcept((byte)0) >= 0)
                    ThrowExceedsMaxValue(bytes);
                return BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(prefixLen));
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static ulong ReadUInt64BigEndian1To7(ReadOnlySpan<byte> s)
            {
                Debug.Assert((uint)s.Length - 1u < 7u);

                ref byte r0 = ref MemoryMarshal.GetReference(s);

                return s.Length switch
                {
                    1 => r0,
                    2 => ((ulong)r0 << 8) | Unsafe.Add(ref r0, 1),
                    3 => ((ulong)r0 << 16) | ((ulong)Unsafe.Add(ref r0, 1) << 8) | Unsafe.Add(ref r0, 2),
                    4 => BinaryPrimitives.ReadUInt32BigEndian(s),
                    5 => ((ulong)BinaryPrimitives.ReadUInt32BigEndian(s) << 8) | Unsafe.Add(ref r0, 4),
                    6 => ((ulong)BinaryPrimitives.ReadUInt32BigEndian(s) << 16) | ((ulong)Unsafe.Add(ref r0, 4) << 8) | Unsafe.Add(ref r0, 5),
                    7 => ((ulong)BinaryPrimitives.ReadUInt32BigEndian(s) << 24) | ((ulong)Unsafe.Add(ref r0, 4) << 16) | ((ulong)Unsafe.Add(ref r0, 5) << 8) | Unsafe.Add(ref r0, 6),
                    _ => 0
                };
            }

            [DoesNotReturn, StackTraceHidden]
            static void ThrowExceedsMaxValue(ReadOnlySpan<byte> bytes)
            {
                BigInteger value = new(bytes, isUnsigned: true, isBigEndian: true);
                throw new OverflowException($"Value {value} exceeds maximum allowed value");
            }
        }

        public static ulong ToULong(this byte[] bytes) => ToULong((ReadOnlySpan<byte>)bytes);

        // Folds two 64-bit words to one with a multiply (mum/wymix): the product is non-linear in both
        // words and spreads each word's high bits into the low output bits. The XOR constants keep an
        // all-zero word from zeroing the product.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static long MumFold(ulong a, ulong b)
        {
            ulong low = Math.BigMul(a ^ 0x9E3779B97F4A7C15UL, b ^ 0xBF58476D1CE4E5B9UL, out ulong high);
            return (long)(low ^ high);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long MumFold(Vector128<byte> mixed)
            => MumFold(mixed.AsUInt64().GetElement(0), mixed.AsUInt64().GetElement(1));

        /// <summary>
        /// Computes a very fast, non-cryptographic 64-bit hash of exactly 32 bytes.
        /// </summary>
        /// <param name="start">Reference to the first byte of the 32-byte input.</param>
        /// <returns>A 64-bit hash value with good distribution across all bits.</returns>
        /// <remarks>
        /// Uses AES hardware acceleration when available. Otherwise, normal builds use process-seeded XXH3 and ZK
        /// builds use their deterministic guest mixer.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long FastHash64For32Bytes(ref byte start)
        {
            if (x64.Aes.IsSupported || Arm.Aes.IsSupported)
            {
                Vector128<byte> key = Unsafe.As<byte, Vector128<byte>>(ref start);
                Vector128<byte> data = Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref start, 16));
                key ^= ComputeAes32Seed();
                // Two AES rounds for full diffusion: after round 1, variation spreads to one column;
                // after round 2, every output byte depends on every input byte.
                Vector128<byte> mixed = FastHashAesRound(data, key);
                mixed = FastHashAesRound(mixed, key);
                return MumFold(mixed);
            }

            return FastHash64For32BytesFallback(ref start);
        }

        // Deterministic ZK fallback. Kept internal for direct distribution tests.
        internal static long FastHash64For32BytesCrc(ref byte start, uint seed)
        {
            ulong h0 = Crc32C(seed, Unsafe.ReadUnaligned<ulong>(ref start));
            ulong h1 = Crc32C(seed ^ 0x9E3779B9u, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, 8)));
            ulong h2 = Crc32C(seed ^ 0x85EBCA6Bu, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, 16)));
            ulong h3 = Crc32C(seed ^ 0xC2B2AE35u, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, 24)));
            return MumFold(h0 | (h1 << 32), h2 | (h3 << 32));
        }

        /// <summary>
        /// Computes a very fast, non-cryptographic 64-bit hash of exactly 20 bytes (Address size).
        /// </summary>
        /// <param name="start">Reference to the first byte of the 20-byte input.</param>
        /// <returns>A 64-bit hash value with good distribution across all bits.</returns>
        /// <remarks>
        /// Uses AES hardware acceleration when available. Otherwise, normal builds use process-seeded XXH3 and ZK
        /// builds use their deterministic guest mixer.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long FastHash64For20Bytes(ref byte start)
        {
            if (x64.Aes.IsSupported || Arm.Aes.IsSupported)
            {
                Vector128<byte> key = Unsafe.As<byte, Vector128<byte>>(ref start);
                uint last4 = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref start, 16));
                Vector128<byte> data = Vector128.CreateScalar(last4).AsByte();
                key ^= ComputeAes20Seed();
                // Two AES rounds for full diffusion: a single round only spreads the varying
                // bytes (16-19) to one column, leaving the low 32 bits of the output constant
                // when bytes 0-15 are constant (e.g., zero-padded small-integer addresses).
                Vector128<byte> mixed = FastHashAesRound(data, key);
                mixed = FastHashAesRound(mixed, key);
                return MumFold(mixed);
            }

            return FastHash64For20BytesFallback(ref start);
        }

        /// <inheritdoc cref="FastHash64For32BytesCrc"/>
        internal static long FastHash64For20BytesCrc(ref byte start, uint seed)
        {
            ulong h0 = Crc32C(seed, Unsafe.ReadUnaligned<ulong>(ref start));
            ulong h1 = Crc32C(seed ^ 0x9E3779B9u, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, 8)));
            uint h2 = Crc32C(seed ^ 0x85EBCA6Bu, Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref start, 16)));
            return MumFold(h0 | (h1 << 32), h2);
        }
    }
}
