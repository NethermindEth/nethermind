// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class KeccakTests
    {
        public const string KeccakOfAnEmptyString = "0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470";
        public const string KeccakZero = "0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470";

        [TestCase(true, "0xc5d246...85a470")]
        [TestCase(false, "c5d246...85a470")]
        public void To_short_string(bool withZeroX, string expected)
        {
            string result = Keccak.OfAnEmptyString.ToShortString(withZeroX);
            Assert.That(result, Is.EqualTo(expected));
        }

        [Test]
        public void Built_digest_short()
        {
            byte[] bytes = new byte[32];
            new Random(42).NextBytes(bytes);

            string result = Keccak.Compute(bytes).ToString();

            KeccakHash keccakHash = KeccakHash.Create();
            keccakHash.Reset();

            for (int i = 0; i < 1024 / 32; i += 32)
            {
                keccakHash.Update(bytes.AsSpan(i, 32));
            }

            Assert.That(keccakHash.Hash.ToHexString(true), Is.EqualTo(result));
        }

        [Test]
        public void Empty_byte_array()
        {
            string result = Keccak.Compute(new byte[] { }).ToString();
            Assert.That(result, Is.EqualTo(KeccakOfAnEmptyString));
        }

        [Test]
        public void Empty_string()
        {
            string result = Keccak.Compute(string.Empty).ToString();
            Assert.That(result, Is.EqualTo(KeccakOfAnEmptyString));
        }

        [Test]
        public void Null_string()
        {
            string result = Keccak.Compute((string)null!).ToString();
            Assert.That(result, Is.EqualTo(KeccakOfAnEmptyString));
        }

        [Test]
        public void Null_bytes()
        {
            string result = Keccak.Compute((byte[])null!).ToString();
            Assert.That(result, Is.EqualTo(KeccakOfAnEmptyString));
        }

        [Test]
        public void Zero()
        {
            string result = Keccak.Zero.ToString();
            Assert.That(result, Is.EqualTo("0x0000000000000000000000000000000000000000000000000000000000000000"));
        }

        [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", null, -1)]
        [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470", -1)]
        [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0x0000000000000000000000000000000000000000000000000000000000000000", 0)]
        [TestCase("0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470", "0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470", 0)]
        [TestCase("0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470", "0x0000000000000000000000000000000000000000000000000000000000000000", 1)]
        public void Compare(string a, string b, int result)
        {
#pragma warning disable CS8600
            Keccak keccakA = a is null ? null : new Keccak(a);
            Keccak keccakB = b is null ? null : new Keccak(b);
#pragma warning restore CS8600
#pragma warning disable CS8602
            Math.Sign(keccakA.CompareTo(keccakB)).Should().Be(result);
#pragma warning restore CS8602
        }

        [Test]
        public void CompareSameInstance()
        {
            Keccak.Zero.CompareTo(Keccak.Zero).Should().Be(0);
        }

        [Test]
        public void Span()
        {
            byte[] byteArray = new byte[1024];
            for (int i = 0; i < byteArray.Length; i++)
            {
                byteArray[i] = (byte)(i % 256);
            }

            Assert.That(Keccak.Compute(byteArray.AsSpan()), Is.EqualTo(Keccak.Compute(byteArray)));
        }
    }
}
