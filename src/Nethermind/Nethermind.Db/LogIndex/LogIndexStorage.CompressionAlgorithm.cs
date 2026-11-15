// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using static Nethermind.TurboPForBindings.TurboPFor;

namespace Nethermind.Db.LogIndex;

partial class LogIndexStorage
{
    /// <summary>
    /// Represents compression algorithm to be used by log index.
    /// </summary>
    public class CompressionAlgorithm(
        string name,
        CompressionAlgorithm.CompressFunc compressionFunc,
        CompressionAlgorithm.DecompressFunc decompressionFunc
    )
    {
        public delegate nuint CompressFunc(ReadOnlySpan<int> @in, nuint n, Span<byte> @out);
        public delegate nuint DecompressFunc(ReadOnlySpan<byte> @in, nuint n, Span<int> @out);

        /// <summary>
        /// Number of values a decoder can write after the last decoded value.
        /// </summary>
        /// <remarks>
        /// TurboPFor adds the exceptions of the last, partial block with 4-value SIMD stores.
        /// </remarks>
        public const int DecompressionOverrun = 3;

        /// <summary>
        /// Number of bytes a decoder can read after the end of the compressed data.
        /// </summary>
        public const int CompressedDataOverread = 3;

        /// <summary>
        /// Returns the largest number of bytes an encoder can write for <paramref name="count"/> values.
        /// </summary>
        /// <remarks>
        /// TurboPFor's <c>p4nbound128v32</c>, which is not smaller than <c>p4nbound256v32</c>,
        /// plus one byte: the first value is stored in 5 bytes when it is 2^28 or larger.
        /// </remarks>
        public static int GetMaxCompressedLength(int count) => count * sizeof(int) + (count + 127) / 128 + 1;

        private static readonly Dictionary<string, CompressionAlgorithm> SupportedMap = [];

        public static IReadOnlyDictionary<string, CompressionAlgorithm> Supported => SupportedMap;

        public static KeyValuePair<string, CompressionAlgorithm> Best =>
            SupportedMap.TryGetValue(nameof(p4nd1enc256v32), out CompressionAlgorithm? p256)
                ? KeyValuePair.Create(nameof(p4nd1enc256v32), p256)
                : KeyValuePair.Create(nameof(p4nd1enc128v32), SupportedMap[nameof(p4nd1enc128v32)]);

        static CompressionAlgorithm()
        {
            SupportedMap.Add(
                nameof(p4nd1enc128v32),
                new(nameof(p4nd1enc128v32), p4nd1enc128v32, p4nd1dec128v32)
            );

            if (Supports256Blocks)
            {
                SupportedMap.Add(
                    nameof(p4nd1enc256v32),
                    new(nameof(p4nd1enc256v32), p4nd1enc256v32, p4nd1dec256v32)
                );
            }
        }

        public string Name => name;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public nuint Compress(ReadOnlySpan<int> @in, nuint n, Span<byte> @out) => compressionFunc(@in, n, @out);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public nuint Decompress(ReadOnlySpan<byte> @in, nuint n, Span<int> @out) => decompressionFunc(@in, n, @out);
    }
}
