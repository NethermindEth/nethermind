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

        [TestCase("0x", "c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470")]
        [TestCase("0x0000000000000000000000000000000017f1d3a73197d7942695638c4fa9ac0fc3688c4f9774b905a14e3a3f171bac586c55e83ff97a1aeffb3af00adb22c6bb0000000000000000000000000000000008b3f481e3aaa0f1a09e30ed741d8ae4fcf5e095d5d00af600db18cb2c04b3edd03cc744a2888ae40caa232946c5e7e1","17324a2fdcdb2cdcf2a8697d8dc07e3ee621d20e7dfb64771438fe28ce1701c1")]
        [TestCase("0x17f1d3a73197d7942695638c4fa9ac0fc3688c4f9774b905a14e3a3f171bac586c55e83ff97a1aeffb3af00adb22c6bb0000000000000000000000000000000008b3f481e3aaa0f1a09e30ed741d8ae4fcf5e095d5d00af600db18cb2c04b3edd03cc744a2888ae40caa232946c5e7e1","937704a0ef8911455fd56477941ba109ccf0fad5aa6bca925ff93396629c9be5")]
        public void Sanity_check(string hexString, string expected)
        {
            byte[] bytes = Bytes.FromHexString(hexString);
            ValueHash256 h = KeccakCache.Compute(bytes);
            h.Bytes.ToHexString().Should().Be(expected);
        }
    }
}
