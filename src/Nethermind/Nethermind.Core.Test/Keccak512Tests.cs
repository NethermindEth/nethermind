// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class Keccak512Tests
    {
        [Test]
        public void Actual_text()
        {
            string result = Keccak512.Compute("123").ToString();
            Assert.AreEqual("0x8ca32d950873fd2b5b34a7d79c4a294b2fd805abe3261beb04fab61a3b4b75609afd6478aa8d34e03f262d68bb09a2ba9d655e228c96723b2854838a6e613b9d", result);
        }
        [Test]
        public void Empty_string()
        {
            string result = Keccak512.Compute(string.Empty).ToString();
            Assert.AreEqual(Keccak512.OfAnEmptyString.ToString(), result);
        }

        [Test]
        public void Null_string()
        {
            string result = Keccak512.Compute((string?)null).ToString();
            Assert.AreEqual(Keccak512.OfAnEmptyString.ToString(), result);
        }

        [Test]
        public void Null_bytes()
        {
            string result = Keccak512.Compute((byte[]?)null).ToString();
            Assert.AreEqual(Keccak512.OfAnEmptyString.ToString(), result);
        }

        [Test]
        public void Zero()
        {
            string result = Keccak512.Zero.ToString();
            Assert.AreEqual("0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000", result);
        }
    }
}
