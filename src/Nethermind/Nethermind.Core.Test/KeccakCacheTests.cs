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
    }
}
