// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using static Nethermind.Db.TurboPFor;

namespace Nethermind.Db;

partial class LogIndexStorage
{
    public class CompressionAlgorithm(
        string name,
        CompressionAlgorithm.CompressFunc compressionFunc,
        CompressionAlgorithm.DecompressFunc decompressionFunc
    )
    {
        public delegate nuint CompressFunc(ReadOnlySpan<int> @in, nuint n, Span<byte> @out);
        public delegate nuint DecompressFunc(ReadOnlySpan<byte> @in, nuint n, Span<int> @out);

        private static readonly Dictionary<string, CompressionAlgorithm> _supported = new();

        public static IReadOnlyDictionary<string, CompressionAlgorithm> Supported => _supported;

        public static KeyValuePair<string, CompressionAlgorithm> Best =>
            _supported.TryGetValue(nameof(p4nd1enc256v32), out CompressionAlgorithm p256)
                ? KeyValuePair.Create(nameof(p4nd1enc256v32), p256)
                : KeyValuePair.Create(nameof(p4nd1enc128v32), _supported[nameof(p4nd1enc128v32)]);

        static CompressionAlgorithm()
        {
            _supported.Add(
                nameof(p4nd1enc128v32),
                new(nameof(p4nd1enc128v32), p4nd1enc128v32, p4nd1dec128v32)
            );

            if (Supports256Blocks)
            {
                _supported.Add(
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
