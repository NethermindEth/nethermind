// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Linq;
using System.Threading.Tasks;
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

            Random random = new(13);
            byte[] bytes = new byte[31]; // misaligned length
            random.NextBytes(bytes);

            ValueHash256 expected = ValueKeccak.Compute(bytes);

            for (int i = 0; i < spins; i++)
            {
                ValueHash256 actual = KeccakCache.Compute(bytes);
                Assert.That(actual.Equals(expected), Is.True);
            }
        }

        [Test]
        public void Empty()
        {
            ReadOnlySpan<byte> span = [];
            Assert.That(KeccakCache.Compute(span), Is.EqualTo(ValueKeccak.Compute(span)));
        }

        [Test]
        public void Very_long()
        {
            ReadOnlySpan<byte> span = new byte[192];
            Assert.That(KeccakCache.Compute(span), Is.EqualTo(ValueKeccak.Compute(span)));
        }

        private string[] GetBucketCollisions()
        {
            Random random = new(13);
            Span<byte> span = stackalloc byte[32];
            string[] collisions = new string[4];

            random.NextBytes(span);
            uint bucket = KeccakCache.GetBucket(span);

            Console.WriteLine(span.ToHexString());

            collisions[0] = span.ToHexString();
            int found = 1;

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
            string[] colliding = GetBucketCollisions();

            int collisions = colliding.Length;
            byte[][] array = colliding.Select(c => Bytes.FromHexString(c)).ToArray();
            ValueHash256[] values = array.Select(a => ValueKeccak.Compute(a)).ToArray();

            uint bucket = KeccakCache.GetBucket(array[0]);

            for (int i = 1; i < collisions; i++)
            {
                byte[] input = array[i];
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(bucket, Is.EqualTo(KeccakCache.GetBucket(input)));
                    Assert.That(KeccakCache.Compute(input), Is.EqualTo(values[i]));
                }
            }

            Parallel.ForEach(array, (a, state, index) =>
            {
                ValueHash256 v = values[index];

                for (int i = 0; i < 100_000; i++)
                {
                    Assert.That(KeccakCache.Compute(a), Is.EqualTo(v));
                }
            });
        }

        [Test]
        public void Spin_through_all()
        {
            Span<byte> span = stackalloc byte[4];
            for (int i = 0; i < (int)KeccakCache.Count; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(span, i);
                Assert.That(KeccakCache.Compute(span), Is.EqualTo(ValueKeccak.Compute(span)));
            }
        }

        [Test]
        public void Hash256_32_byte_path()
        {
            // Tests the optimized 32-byte path (most common - Hash256/UInt256)
            Random random = new(42);
            for (int i = 0; i < 1000; i++)
            {
                byte[] bytes = new byte[32];
                random.NextBytes(bytes);

                ValueHash256 expected = ValueKeccak.Compute(bytes);
                ValueHash256 actual = KeccakCache.Compute(bytes);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(actual, Is.EqualTo(expected));

                    // Second call should hit cache
                    Assert.That(KeccakCache.Compute(bytes), Is.EqualTo(expected));
                }
            }
        }

        [Test]
        public void Address_20_byte_path()
        {
            // Tests the optimized 20-byte path (Address)
            Random random = new(42);
            for (int i = 0; i < 1000; i++)
            {
                byte[] bytes = new byte[20];
                random.NextBytes(bytes);

                ValueHash256 expected = ValueKeccak.Compute(bytes);
                ValueHash256 actual = KeccakCache.Compute(bytes);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(actual, Is.EqualTo(expected));

                    // Second call should hit cache
                    Assert.That(KeccakCache.Compute(bytes), Is.EqualTo(expected));
                }
            }
        }

        [Test]
        public void Concurrent_read_write_stress()
        {
            // Stress test the seqlock pattern with concurrent readers and writers
            const int iterations = 100_000;
            byte[] bytes = new byte[32];
            new Random(123).NextBytes(bytes);

            // Prime the cache
            KeccakCache.Compute(bytes);

            // Create different keys that hash to the same bucket (will cause cache eviction)
            byte[][] collisions = new byte[4][];
            collisions[0] = bytes;
            Random random = new(456);
            uint bucket = KeccakCache.GetBucket(bytes);
            int found = 1;
            while (found < 4)
            {
                byte[] candidate = new byte[32];
                random.NextBytes(candidate);
                if (KeccakCache.GetBucket(candidate) == bucket)
                {
                    collisions[found++] = candidate;
                }
            }

            ValueHash256[] expectedValues = collisions.Select(c => ValueKeccak.Compute(c)).ToArray();

            // Parallel readers and writers hammering the same bucket
            Parallel.For(0, iterations, i =>
            {
                int idx = i % 4;
                byte[] input = collisions[idx];
                ValueHash256 result = KeccakCache.Compute(input);
                Assert.That(result, Is.EqualTo(expectedValues[idx]), $"iteration {i}, index {idx}");
            });
        }
    }
}
