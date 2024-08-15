// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Caching;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Crypto
{
    [DebuggerStepThrough]
    [DebuggerDisplay("{ToString()}")]
    public static class ValueKeccak
    {
        private static readonly HashCache _cache = new();
        /// <returns>
        ///     <string>0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470</string>
        /// </returns>
        public static readonly ValueHash256 OfAnEmptyString = new("0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470");

        /// <returns>
        ///     <string>0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347</string>
        /// </returns>
        public static readonly ValueHash256 OfAnEmptySequenceRlp = InternalCompute([192]);

        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static readonly ValueHash256 EmptyTreeHash = InternalCompute([128]);

        /// <returns>
        ///     <string>0x0000000000000000000000000000000000000000000000000000000000000000</string>
        /// </returns>
        public static ValueHash256 Zero { get; } = default;

        /// <summary>
        ///     <string>0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff</string>
        /// </summary>
        public static ValueHash256 MaxValue { get; } = new("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

        [DebuggerStepThrough]
        public static ValueHash256 Compute(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return OfAnEmptyString;
            }

            return InternalCompute(System.Text.Encoding.UTF8.GetBytes(input));
        }

        private readonly struct BytesWrapper(byte[] bytes) : IEquatable<BytesWrapper>
        {
            internal readonly byte[] _bytes = bytes;
            public static implicit operator BytesWrapper(byte[] bytes) => new(bytes);
            public static implicit operator BytesWrapper(ReadOnlySpan<byte> bytes) => new(bytes.ToArray());
            public bool Equals(BytesWrapper other) => Bytes.EqualityComparer.Equals(_bytes, other._bytes);
            public override bool Equals(object? obj) => obj is BytesWrapper other && Equals(other);
            public override int GetHashCode() => Bytes.EqualityComparer.GetHashCode(_bytes);
        }

        [SkipLocalsInit]
        [DebuggerStepThrough]
        public static ValueHash256 Compute(ReadOnlySpan<byte> input)
        {
            if (input.Length == 0)
            {
                return OfAnEmptyString;
            }

            Unsafe.SkipInit(out ValueHash256 hash);
            Unsafe.SkipInit(out BytesWrapper cacheKey);
            if (input.Length <= 32)
            {
                cacheKey = input;
                if (_cache.TryGet(cacheKey, out hash)) return hash;
            }

            KeccakHash.ComputeHashBytesToSpan(input, MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref hash, 1)));
            if (input.Length <= 32)
            {
                _cache.Set(cacheKey, hash);
            }
            return hash;
        }

        internal static ValueHash256 InternalCompute(byte[] input)
        {
            if (input is null || input.Length == 0)
            {
                return OfAnEmptyString;
            }

            Unsafe.SkipInit(out ValueHash256 hash);
            if (input.Length <= 32 && _cache.TryGet(input, out hash))
            {
                return hash;
            }

            KeccakHash.ComputeHashBytesToSpan(input, MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref hash, 1)));
            if (input.Length <= 32)
            {
                _cache.Set(input, hash);
            }
            return hash;
        }

        private sealed class HashCache
        {
            private const int CacheCount = 16;
            private const int CacheMax = CacheCount - 1;
            private readonly ClockCache<BytesWrapper, ValueHash256>[] _caches;

            public HashCache()
            {
                _caches = new ClockCache<BytesWrapper, ValueHash256>[CacheCount];
                for (int i = 0; i < _caches.Length; i++)
                {
                    // Cache per nibble to reduce contention as is very parallel
                    _caches[i] = new ClockCache<BytesWrapper, ValueHash256>(4096);
                }
            }

            public bool Set(BytesWrapper bytes, in ValueHash256 hash)
            {
                ClockCache<BytesWrapper, ValueHash256> cache = _caches[GetCacheIndex(bytes)];
                return cache.Set(bytes, hash);
            }

            private static int GetCacheIndex(BytesWrapper bytes) => bytes._bytes[^1] & CacheMax;

            public bool TryGet(BytesWrapper bytes, out ValueHash256 hash)
            {
                ClockCache<BytesWrapper, ValueHash256> cache = _caches[GetCacheIndex(bytes)];
                return cache.TryGet(bytes, out hash);
            }
        }
    }

    [DebuggerStepThrough]
    public static class Keccak
    {
        public const int Size = 32;

        public const int MemorySize =
            MemorySizes.SmallObjectOverhead -
            MemorySizes.RefSize +
            Size;

        /// <returns>
        ///     <string>0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470</string>
        /// </returns>
        public static readonly Hash256 OfAnEmptyString = new(ValueKeccak.OfAnEmptyString);

        /// <returns>
        ///     <string>0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347</string>
        /// </returns>
        public static readonly Hash256 OfAnEmptySequenceRlp = new(ValueKeccak.OfAnEmptySequenceRlp);

        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static Hash256 EmptyTreeHash = new(ValueKeccak.EmptyTreeHash);

        /// <returns>
        ///     <string>0x0000000000000000000000000000000000000000000000000000000000000000</string>
        /// </returns>
        public static Hash256 Zero { get; } = new(new byte[Size]);

        /// <summary>
        ///     <string>0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff</string>
        /// </summary>
        public static Hash256 MaxValue { get; } = new("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");


        [DebuggerStepThrough]
        public static Hash256 Compute(byte[]? input)
        {
            if (input is null || input.Length == 0)
            {
                return OfAnEmptyString;
            }

            return new Hash256(KeccakHash.ComputeHashBytes(input));
        }

        [DebuggerStepThrough]
        public static Hash256 Compute(ReadOnlySpan<byte> input)
        {
            return new Hash256(ValueKeccak.Compute(input));
        }

        [DebuggerStepThrough]
        public static Hash256 Compute(string input)
        {
            return new Hash256(ValueKeccak.Compute(input));
        }
    }
}
