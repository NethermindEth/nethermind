using System;
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
    }
}
