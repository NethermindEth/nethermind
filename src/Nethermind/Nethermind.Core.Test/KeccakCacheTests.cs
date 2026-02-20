// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class KeccakCacheTests
    {
        [Test]
        public void Multiple()
        {
            const int spins = 10;

            var random = new Random(13);
            var bytes = new byte[31]; // misaligned length
            random.NextBytes(bytes);

            ValueHash256 expected = ValueKeccak.Compute(bytes);

            for (int i = 0; i < spins; i++)
            {
                ValueHash256 actual = KeccakCache.Compute(bytes);
                actual.Equals(expected).Should().BeTrue();
            }
        }

        [Test]
        public void Empty()
        {
            ReadOnlySpan<byte> span = [];
            KeccakCache.Compute(span).Should().Be(ValueKeccak.Compute(span));
        }

        [Test]
        public void Very_long()
        {
            ReadOnlySpan<byte> span = new byte[192];
            KeccakCache.Compute(span).Should().Be(ValueKeccak.Compute(span));
        }

        private string[] GetBucketCollisions()
        {
            var random = new Random(13);
            Span<byte> span = stackalloc byte[32];
            string[] collisions = new string[4];

            random.NextBytes(span);
            var bucket = KeccakCache.GetBucket(span);

            Console.WriteLine(span.ToHexString());

            collisions[0] = span.ToHexString();
            var found = 1;

            ulong iterations = 0;
            while (found < 4)
            {
                random.NextBytes(span);
                if (KeccakCache.GetBucket(span) == bucket)
                {
                    collisions[found] = span.ToHexString();
                    Console.WriteLine(span.ToHexString());
                    found++;
                }
                iterations++;
            }

            Console.WriteLine($"{iterations} iterations to find");
            return collisions;
        }

        [Test]
        public void Collision()
        {
            var colliding = GetBucketCollisions();

            var collisions = colliding.Length;
            var array = colliding.Select(c => Bytes.FromHexString(c)).ToArray();
            var values = array.Select(a => ValueKeccak.Compute(a)).ToArray();

            var bucket = KeccakCache.GetBucket(array[0]);

            for (int i = 1; i < collisions; i++)
            {
                var input = array[i];
                bucket.Should().Be(KeccakCache.GetBucket(input));
                KeccakCache.Compute(input).Should().Be(values[i]);
            }

            Parallel.ForEach(array, (a, state, index) =>
            {
                ValueHash256 v = values[index];

                for (int i = 0; i < 100_000; i++)
                {
                    KeccakCache.Compute(a).Should().Be(v);
                }
            });
        }

        [Test]
        public void Spin_through_all()
        {
            Span<byte> span = stackalloc byte[4];
            for (int i = 0; i < KeccakCache.Count; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(span, i);
                KeccakCache.Compute(span).Should().Be(ValueKeccak.Compute(span));
            }
        }

        [Test]
        public void Hash256_32_byte_path()
        {
            // Tests the optimized 32-byte path (most common - Hash256/UInt256)
            var random = new Random(42);
            for (int i = 0; i < 1000; i++)
            {
                var bytes = new byte[32];
                random.NextBytes(bytes);

                ValueHash256 expected = ValueKeccak.Compute(bytes);
                ValueHash256 actual = KeccakCache.Compute(bytes);
                actual.Should().Be(expected);

                // Second call should hit cache
                KeccakCache.Compute(bytes).Should().Be(expected);
            }
        }

        [Test]
        public void Address_20_byte_path()
        {
            // Tests the optimized 20-byte path (Address)
            var random = new Random(42);
            for (int i = 0; i < 1000; i++)
            {
                var bytes = new byte[20];
                random.NextBytes(bytes);

                ValueHash256 expected = ValueKeccak.Compute(bytes);
                ValueHash256 actual = KeccakCache.Compute(bytes);
                actual.Should().Be(expected);

                // Second call should hit cache
                KeccakCache.Compute(bytes).Should().Be(expected);
            }
        }

        [Test]
        public void Concurrent_read_write_stress()
        {
            // Stress test the seqlock pattern with concurrent readers and writers
            const int iterations = 100_000;
            var bytes = new byte[32];
            new Random(123).NextBytes(bytes);

            // Prime the cache
            KeccakCache.Compute(bytes);

            // Create different keys that hash to the same bucket (will cause cache eviction)
            var collisions = new byte[4][];
            collisions[0] = bytes;
            var random = new Random(456);
            var bucket = KeccakCache.GetBucket(bytes);
            int found = 1;
            while (found < 4)
            {
                var candidate = new byte[32];
                random.NextBytes(candidate);
                if (KeccakCache.GetBucket(candidate) == bucket)
                {
                    collisions[found++] = candidate;
                }
            }

            var expectedValues = collisions.Select(c => ValueKeccak.Compute(c)).ToArray();

            // Parallel readers and writers hammering the same bucket
            Parallel.For(0, iterations, i =>
            {
                int idx = i % 4;
                var input = collisions[idx];
                var result = KeccakCache.Compute(input);
                result.Should().Be(expectedValues[idx], $"iteration {i}, index {idx}");
            });
        }
    }
}
