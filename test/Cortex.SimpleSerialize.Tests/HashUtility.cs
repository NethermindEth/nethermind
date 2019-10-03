using System;
using System.Linq;
using System.Security.Cryptography;

namespace Cortex.SimpleSerialize.Tests
{
    public static class HashUtility
    {
        private static readonly HashAlgorithm hash = SHA256.Create();

        public static byte[] Hash(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            var combined = new Span<byte>(new byte[64]);
            a.CopyTo(combined);
            b.CopyTo(combined.Slice(32));
            return hash.ComputeHash(combined.ToArray());
        }

        public static ReadOnlySpan<byte> Chunk(ReadOnlySpan<byte> input)
        {
            var chunk = new Span<byte>(new byte[32]);
            input.CopyTo(chunk);
            return chunk;
        }

        public static ReadOnlySpan<byte> Merge(ReadOnlySpan<byte> input1, ReadOnlySpan<byte> input2)
        {
            var result = new Span<byte>(new byte[input1.Length + input2.Length]);
            input1.CopyTo(result);
            input2.CopyTo(result.Slice(input1.Length));
            return result;
        }

        public static ReadOnlySpan<byte> ZeroHashes(int start, int end)
        {
            return new byte[32];
        }
    }
}
