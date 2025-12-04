// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using static Nethermind.Db.LogIndex.TurboPFor2;

namespace Nethermind.Db.LogIndex;

partial class LogIndexStorage
{
    /// <summary>
    /// Represents compression algorithm to be used by log index.
    /// </summary>
    public class CompressionAlgorithm2(
        string name,
        CompressionAlgorithm2.CompressFunc compressionFunc,
        CompressionAlgorithm2.DecompressFunc decompressionFunc
    )
    {
        public delegate nuint CompressFunc(ReadOnlySpan<long> @in, nuint n, Span<byte> @out);
        public delegate nuint DecompressFunc(ReadOnlySpan<byte> @in, nuint n, Span<long> @out);

        private static readonly Dictionary<string, CompressionAlgorithm2> SupportedMap = new();

        public static IReadOnlyDictionary<string, CompressionAlgorithm2> Supported => SupportedMap;

        public static KeyValuePair<string, CompressionAlgorithm2> Best =>
            KeyValuePair.Create(nameof(p4nd1enc64), SupportedMap[nameof(p4nd1enc64)]);

        static CompressionAlgorithm2()
        {
            // TODO: figure out what to do if not
            if (Supports256Blocks)
            {
                SupportedMap.Add(
                    nameof(p4nd1enc64),
                    new(nameof(p4nd1enc64), p4nd1enc64, p4nd1dec64)
                );
            }
        }

        public string Name => name;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public nuint Compress(ReadOnlySpan<long> @in, nuint n, Span<byte> @out) => compressionFunc(@in, n, @out);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public nuint Decompress(ReadOnlySpan<byte> @in, nuint n, Span<long> @out) => decompressionFunc(@in, n, @out);
    }
}
