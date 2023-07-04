// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;

namespace Nethermind.Serialization.Ssz.Test
{
    public static class HashUtility
    {
        private static readonly HashAlgorithm _hashAlgorithm = SHA256.Create();

        private static readonly byte[][] _zeroHashes;

        static HashUtility()
        {
            _zeroHashes = new byte[32][];
            _zeroHashes[0] = new byte[32];
            for (var height = 1; height < 32; height++)
            {
                _zeroHashes[height] = Hash(_zeroHashes[height - 1], _zeroHashes[height - 1]);
            }
        }

        public static ReadOnlySpan<byte> Chunk(ReadOnlySpan<byte> input)
        {
            var chunk = new Span<byte>(new byte[32]);
            input.CopyTo(chunk);
            return chunk;
        }

        public static byte[] Hash(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            var combined = new Span<byte>(new byte[64]);
            a.CopyTo(combined);
            b.CopyTo(combined[32..]);
            return _hashAlgorithm.ComputeHash(combined.ToArray());
        }

        public static ReadOnlySpan<byte> Merge(ReadOnlySpan<byte> a, byte[][] branch)
        {
            var result = a;
            foreach (var b in branch)
            {
                result = Hash(result, b);
            }
            return result;
        }

        public static byte[][] ZeroHashes(int start, int end)
        {
            return _zeroHashes[start..end];
        }
    }
}
