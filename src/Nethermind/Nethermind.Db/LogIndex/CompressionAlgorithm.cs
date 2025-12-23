// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using static Nethermind.Db.LogIndex.TurboPFor2;

namespace Nethermind.Db.LogIndex;

partial class LogIndexStorage<TPosition>
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
        public delegate nuint CompressFunc(ReadOnlySpan<TPosition> @in, nuint n, Span<byte> @out);
        public delegate nuint DecompressFunc(ReadOnlySpan<byte> @in, nuint n, Span<TPosition> @out);

        private static readonly Dictionary<string, CompressionAlgorithm> SupportedMap = new();

        public static IReadOnlyDictionary<string, CompressionAlgorithm> Supported => SupportedMap;

        public static KeyValuePair<string, CompressionAlgorithm> Best =>
            KeyValuePair.Create(nameof(p4nd1enc64), SupportedMap[nameof(p4nd1enc64)]);

        static CompressionAlgorithm()
        {
            // TODO: check if supported without AVX2
            SupportedMap.Add(
                nameof(p4nd1enc64),
                new(nameof(p4nd1enc64), p4nd1enc64, p4nd1dec64)
            );
        }

        public string Name => name;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public nuint Compress(ReadOnlySpan<TPosition> @in, nuint n, Span<byte> @out) => compressionFunc(@in, n, @out);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public nuint Decompress(ReadOnlySpan<byte> @in, nuint n, Span<TPosition> @out) => decompressionFunc(@in, n, @out);
    }
}
