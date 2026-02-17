// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.KeyStore.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class CryptoRandomTests
    {
        [Test]
        public void NextInt_ReturnsValueWithinBounds()
        {
            using CryptoRandom rng = new();

            int[] bounds = { 1, 2, 10, 100, 1024 };
            foreach (int max in bounds)
            {
                int value = rng.NextInt(max);
                Assert.That(value, Is.GreaterThanOrEqualTo(0).And.LessThan(max));
            }
        }

        [Test]
        public void NextInt_ZeroOrNegative_ReturnsZero()
        {
            using CryptoRandom rng = new();

            Assert.That(rng.NextInt(0), Is.EqualTo(0));
            Assert.That(rng.NextInt(-1), Is.EqualTo(0));
        }

        [Test]
        public void GenerateRandomBytes_FillsAndVariesBetweenCalls()
        {
            using CryptoRandom rng = new();

            byte[] a = rng.GenerateRandomBytes(32);
            byte[] b = rng.GenerateRandomBytes(32);

            // Ensure arrays are correct length and not all zeros
            Assert.That(a.Length, Is.EqualTo(32));
            Assert.That(b.Length, Is.EqualTo(32));
            Assert.That(a.Any(x => x != 0), Is.True);
            Assert.That(b.Any(x => x != 0), Is.True);

            // Extremely unlikely to fail: values should differ
            Assert.That(a.SequenceEqual(b), Is.False);
        }

        [Test]
        public void GenerateRandomBytes_SpanFilled()
        {
            using CryptoRandom rng = new();

            Span<byte> span = stackalloc byte[16];
            rng.GenerateRandomBytes(span);

            Assert.That(span.ToArray().Any(x => x != 0), Is.True);
        }
    }
}
