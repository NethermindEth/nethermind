// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Arm = System.Runtime.Intrinsics.Arm;
using x64 = System.Runtime.Intrinsics.X86;
using Nethermind.Core.Collections;
using Nethermind.Int256;

namespace Nethermind.Core.Extensions
{
    public static partial class SpanExtensions
    {
        private const ulong ShortInputDomain = 0xD6E8FEB86659FD93UL;

        /// <summary>Installs the guest hash mixers' per-run seed.</summary>
        /// <param name="seed">The full-width 256-bit seed for this run.</param>
        /// <remarks>
        /// The guest currently seeds from <c>new_payload_request_root</c>, providing payload-dependent
        /// hashing without an additional randomness input. This seed is public and identical across
        /// provers and retries for the same payload, so pathological collisions are shared.
        /// Fresh prover-private cryptographic randomness would provide independent hash layouts per
        /// proof attempt; standard support is proposed in
        /// <see href="https://github.com/eth-act/zkevm-standards/issues/41">eth-act/zkevm-standards#41</see>.
        /// <para>
        /// Install the entire seed before creating hash-keyed containers;
        /// reseeding invalidates stored hashes. Not synchronised against concurrent hashing.
        /// In release guests, unseeded scalar hashing of 32-byte and other inputs longer than 16 bytes
        /// (except 20-byte addresses) silently uses a zero seed; address and short-input hashing access
        /// uninitialised seed arrays. Missing seeding is not guaranteed to fail immediately.
        /// </para>
        /// <para>
        /// A no-op on the host, which seeds its hashes per process. The guest installs its seed at run
        /// time to avoid a static constructor and its initialisation checks on hash calls.
        /// These mixers are not cryptographic authentication functions.
        /// </para>
        /// </remarks>
        public static partial void SeedHashes(in UInt256 seed);

        /// <summary>Combines a hash with the next value for in-memory bucketing.</summary>
        /// <remarks>Uses CRC on the host and the run-seeded mixer in the guest.</remarks>
        public static partial int CombineHash(uint hash, ulong value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Vector128<byte> ComputeAesSeed(int len)
        {
            ulong lengthSalt = (uint)len;
            lengthSalt |= lengthSalt << 32;
            return AesHashSeed ^ Vector128.Create(lengthSalt).AsByte();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Vector128<byte> ComputeAesFinalSeed()
            => AesHashFinalSeed;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> ComputeAes20Seed()
            => AesHash20Seed;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> ComputeAes32Seed()
            => AesHash32Seed;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> ComputeAesPairSeed()
            => AesHashPairSeed;

        // Round constants for FastHash64ForAddressAndSlot. Public values, distinct from each other; the secrecy
        // is the seed they are combined with.
        private static Vector128<byte> PairRound2 => Vector128.Create(0x9E3779B97F4A7C15UL, 0xBF58476D1CE4E5B9UL).AsByte();
        private static Vector128<byte> PairRound3 => Vector128.Create(0x94D049BB133111EBUL, 0x2545F4914F6CDD1DUL).AsByte();
        private static Vector128<byte> PairRound4 => Vector128.Create(0xD6E8FEB86659FD93UL, 0xA0761D6478BD642FUL).AsByte();
        private static Vector128<byte> PairRound5 => Vector128.Create(0xE7037ED1A0B428DBUL, 0x8EBC6AF09C88C6E3UL).AsByte();

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
        /// hardware acceleration is available. Otherwise, both builds use the seeded scalar mixer. ZK builds use
        /// a deterministic seed installed before execution.
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
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong ReadPartialWord(ref byte p, int length)
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
                // len == 16 aliases the block that built acc0 as the round key. Safe because
                // FastHashAesRound XORs the key in after SubBytes, so key and state cannot cancel.
                Vector128<byte> data = Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref start, len - 16));
                acc0 = FastHashAesRound(acc0, data);
            }

            return (int)MumFold(FastHashAesRound(acc0, ComputeAesFinalSeed()));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> FastHashAesRound(Vector128<byte> state, Vector128<byte> roundKey)
            => x64.Aes.IsSupported
                ? x64.Aes.Encrypt(state, roundKey)
                // Keep the round key outside AESE so state and roundKey have distinct roles in the mixer.
                : Arm.Aes.MixColumns(Arm.Aes.Encrypt(state, Vector128<byte>.Zero)) ^ roundKey;

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
            // Hex, not decimal: rendering the value needed a BigInteger, and that linked
            // System.Runtime.Numerics for the sake of a message on a throwing path.
            static void ThrowExceedsMaxValue(ReadOnlySpan<byte> bytes) =>
                throw new OverflowException($"Value 0x{bytes.ToHexString()} exceeds maximum allowed value");
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
            // Hex, not decimal: rendering the value needed a BigInteger, and that linked
            // System.Runtime.Numerics for the sake of a message on a throwing path.
            static void ThrowExceedsMaxValue(ReadOnlySpan<byte> bytes) =>
                throw new OverflowException($"Value 0x{bytes.ToHexString()} exceeds maximum allowed value");
        }

        public static ulong ToULong(this byte[] bytes) => ToULong((ReadOnlySpan<byte>)bytes);

        // Folds two 64-bit words to one with a multiply (mum/wymix): the product is non-linear in both
        // words and spreads each word's high bits into the low output bits. The XOR constants move each
        // factor's zeroing input off the common all-zero word; a factor still degenerates when a word
        // equals its constant, which seeded inputs hit with probability 2^-64.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long MumFold(ulong a, ulong b)
            => (long)MultiplyFold(a ^ 0x9E3779B97F4A7C15UL, b ^ 0xBF58476D1CE4E5B9UL);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long MumFold(Vector128<byte> mixed)
            => MumFold(mixed.AsUInt64().GetElement(0), mixed.AsUInt64().GetElement(1));

        /// <summary>
        /// Computes a very fast, non-cryptographic 64-bit hash of exactly 32 bytes.
        /// </summary>
        /// <param name="start">Reference to the first byte of the 32-byte input.</param>
        /// <returns>A 64-bit hash value with good distribution across all bits.</returns>
        /// <remarks>
        /// Uses AES hardware acceleration when available. Otherwise, both builds use the seeded scalar mixer. ZK
        /// builds use a deterministic seed installed before execution.
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
                mixed = FastHashAesRound(mixed, key ^ ComputeAesFinalSeed());
                return MumFold(mixed);
            }

            return FastHash64For32BytesFallback(ref start);
        }

        /// <summary>
        /// Computes a very fast, non-cryptographic 64-bit hash of exactly 20 bytes (Address size).
        /// </summary>
        /// <param name="start">Reference to the first byte of the 20-byte input.</param>
        /// <returns>A 64-bit hash value with good distribution across all bits.</returns>
        /// <remarks>
        /// Uses AES hardware acceleration when available. Otherwise, both builds use the seeded scalar mixer. ZK
        /// builds use a deterministic seed installed before execution.
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
                mixed = FastHashAesRound(mixed, key ^ ComputeAesFinalSeed());
                return MumFold(mixed);
            }

            return FastHash64For20BytesFallback(ref start);
        }

        /// <summary>
        /// Computes a 64-bit hash of a 20-byte address paired with a 32-byte slot index.
        /// </summary>
        /// <param name="address">Reference to the first byte of the 20-byte address.</param>
        /// <param name="index">Reference to the first byte of the 32-byte slot index.</param>
        /// <returns>A 64-bit hash with good distribution across all bits.</returns>
        /// <remarks>
        /// One AES chain over both inputs where AES is available, instead of hashing each and folding the two.
        /// The fallback keeps the older form.
        /// <para>
        /// Each part of the key is the data of a round of its own, and no part is ever a round key. So no two
        /// parts share a word, and a difference in one can only be cancelled by evaluating a round, which needs
        /// the seed. A part used as a round key would share a word with the next round's data, and such a pair is
        /// found offline at the birthday bound over addresses an attacker grinds. That is the flaw the earlier
        /// four-lane XOR fold had, and it is a cache-flood lever.
        /// </para>
        /// <para>
        /// Four parts need five rounds, because the last one injected needs a second round to spread its
        /// difference past one column. Round keys alternate between the seed halves with distinct constants.
        /// </para>
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long FastHash64ForAddressAndSlot(ref byte address, ref byte index)
        {
            if (x64.Aes.IsSupported || Arm.Aes.IsSupported)
            {
                Vector128<byte> seed = ComputeAesPairSeed();
                Vector128<byte> finalSeed = ComputeAesFinalSeed();
                Vector128<byte> addressHead = Unsafe.As<byte, Vector128<byte>>(ref address);
                uint addressTail = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref address, 16));
                Vector128<byte> indexLow = Unsafe.As<byte, Vector128<byte>>(ref index);
                Vector128<byte> indexHigh = Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref index, 16));

                Vector128<byte> mixed = FastHashAesRound(addressHead ^ seed, seed);
                mixed = FastHashAesRound(mixed ^ Vector128.CreateScalar(addressTail).AsByte(), finalSeed ^ PairRound2);
                mixed = FastHashAesRound(mixed ^ indexLow, seed ^ PairRound3);
                mixed = FastHashAesRound(mixed ^ indexHigh, finalSeed ^ PairRound4);
                mixed = FastHashAesRound(mixed, seed ^ PairRound5);
                return MumFold(mixed);
            }

            return MumFold((ulong)FastHash64For32Bytes(ref index), (ulong)FastHash64For20Bytes(ref address));
        }

        private const int WordWidth = 32;

        // Width-specific nonlinear derivation keeps addresses separate from padded 32-byte keys.
        private static ulong DeriveAddressSeed(ulong word)
        {
            word += 0x9E3779B97F4A7C15UL + Address.Size;
            word = (word ^ (word >> 30)) * 0xBF58476D1CE4E5B9UL;
            word = (word ^ (word >> 27)) * 0x94D049BB133111EBUL;
            return word ^ (word >> 31);
        }

        [Conditional("DEBUG")]
        private static void AssertSeeded() =>
            Debug.Assert(AddressSeeds is not null, $"{nameof(SeedHashes)} must run before the guest hashes a key.");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int FastHashFallback(ReadOnlySpan<byte> input)
        {
            AssertSeeded();
            ulong hash = input.Length switch
            {
                WordWidth => Mix32(ref MemoryMarshal.GetReference(input)),
                Address.Size => MixAddress(ref MemoryMarshal.GetReference(input)),
                <= 16 => MixShortBytes(input),
                _ => MixBytes(input)
            };
            return (int)(hash ^ (hash >> 32));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MixAddress(ref byte b)
        {
            AssertSeeded();
            ref ulong seeds = ref MemoryMarshal.GetArrayDataReference(AddressSeeds!);
            return MixWords(
                Unsafe.ReadUnaligned<ulong>(ref b),
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8)),
                Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref b, 16)), 0, ref seeds);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static long FastHash64For32BytesFallback(ref byte start)
            => (long)Mix32(ref start);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Mix32(ref byte b)
        {
            AssertSeeded();
            ref ulong seeds = ref Unsafe.As<UInt256, ulong>(ref Unsafe.AsRef(in InstanceRandom));
            return MixWords(
                Unsafe.ReadUnaligned<ulong>(ref b),
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8)),
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 16)),
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 24)), ref seeds);
        }

#if ZK_EVM
        // Keep a call boundary: the RISC-V backend can omit the int truncation after an inlined multiply-fold.
        [MethodImpl(MethodImplOptions.NoInlining)]
#else
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private static ulong MixWords(ulong u0, ulong u1, ulong u2, ulong u3, ref ulong seeds)
        {
            // Mix each seed limb into its key limb before any information is lost to folding.
            ulong a = MultiplyFold(u0 ^ seeds, u1 ^ Unsafe.Add(ref seeds, 1));
            ulong b = MultiplyFold(u2 ^ Unsafe.Add(ref seeds, 2), u3 ^ Unsafe.Add(ref seeds, 3));
            return (ulong)MumFold(a, b);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MultiplyFold(ulong a, ulong b)
        {
#if ZK_EVM
            uint al = (uint)a, ah = (uint)(a >> 32);
            uint bl = (uint)b, bh = (uint)(b >> 32);
            ulong lower = (ulong)al * bl;
            ulong middle = (ulong)ah * bl + (lower >> 32);
            ulong carry = (ulong)al * bh + (uint)middle;
            ulong low = (carry << 32) | (uint)lower;
            ulong high = (ulong)ah * bh + (middle >> 32) + (carry >> 32);
            return low ^ high;
#else
            ulong high = Math.BigMul(a, b, out ulong low);
            return low ^ high;
#endif
        }

        private static ulong[] CreateShortHashSeeds(in UInt256 seed)
        {
            ulong[] hashes = new ulong[17];
            ref ulong words = ref Unsafe.As<UInt256, ulong>(ref Unsafe.AsRef(in seed));
            for (int length = 0; length < hashes.Length; length++)
                hashes[length] = MixWords((uint)length, ShortInputDomain, 0, 0, ref words);
            return hashes;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadShortWords(ReadOnlySpan<byte> input, out ulong low, out ulong high)
        {
            ref byte start = ref MemoryMarshal.GetReference(input);
            high = 0;
            if (input.Length >= sizeof(ulong))
            {
                low = Unsafe.ReadUnaligned<ulong>(ref start);
                high = input.Length == 16
                    ? Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, sizeof(ulong)))
                    : ReadPartialWord(ref Unsafe.Add(ref start, sizeof(ulong)), input.Length - sizeof(ulong));
            }
            else
            {
                low = ReadPartialWord(ref start, input.Length);
            }
        }

        private static ulong MixShortBytes(ReadOnlySpan<byte> input)
        {
            ref ulong seeds = ref Unsafe.As<UInt256, ulong>(ref Unsafe.AsRef(in InstanceRandom));
            ulong hash = ShortHashSeeds![input.Length];
            ReadShortWords(input, out ulong low, out ulong high);
            ulong tail = MultiplyFold(low ^ seeds, high ^ Unsafe.Add(ref seeds, 1));
            if (input.Length == 16)
            {
                hash = (ulong)MumFold(hash ^ tail, Unsafe.Add(ref seeds, 2));
                tail = MultiplyFold(seeds, Unsafe.Add(ref seeds, 1));
            }
            return (ulong)MumFold(hash ^ tail, Unsafe.Add(ref seeds, 3));
        }

        private static ulong MixBytes(ReadOnlySpan<byte> input)
        {
            ref ulong seeds = ref Unsafe.As<UInt256, ulong>(ref Unsafe.AsRef(in InstanceRandom));
            ulong hash = MixWords((uint)input.Length, ShortInputDomain, 0, 0, ref seeds);
            while (input.Length >= 16)
            {
                ref byte start = ref MemoryMarshal.GetReference(input);
                ulong block = MultiplyFold(Unsafe.ReadUnaligned<ulong>(ref start) ^ seeds,
                    Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, 8)) ^ Unsafe.Add(ref seeds, 1));
                hash = (ulong)MumFold(hash ^ block, Unsafe.Add(ref seeds, 2));
                input = input[16..];
            }

            // The length participates before the blocks, so zero-padding the tail is unambiguous.
            ReadShortWords(input, out ulong low, out ulong high);
            ulong tail = MultiplyFold(low ^ seeds, high ^ Unsafe.Add(ref seeds, 1));
            return (ulong)MumFold(hash ^ tail, Unsafe.Add(ref seeds, 3));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static long FastHash64For20BytesFallback(ref byte start)
            => (long)MixAddress(ref start);
    }
}
