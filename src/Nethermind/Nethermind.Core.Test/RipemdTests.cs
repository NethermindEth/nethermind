// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class RipemdTests
    {
        public const string RipemdOfEmptyString = "0000000000000000000000009c1185a5c5e9fc54612808977ee8f548b2258d31";

        [Test]
        public void Empty_byte_array()
        {
            string result = Ripemd.ComputeString([]);
            Assert.That(result, Is.EqualTo(RipemdOfEmptyString));
        }

        [TestCase("", "0000000000000000000000009c1185a5c5e9fc54612808977ee8f548b2258d31")]
        [TestCase("abc", "0000000000000000000000008eb208f7e05d987a9b044a8e98c6b087f15a0bfc")]
        [TestCase("message digest", "0000000000000000000000005d0689ef49d2fae572b881b123a85ffa21595f36")]
        [TestCase("abcdefghijklmnopqrstuvwxyz", "000000000000000000000000f71c27109c692c1b56bbdceb5b9d2865b3708dbc")]
        public void Known_vectors(string ascii, string expected)
        {
            string result = Ripemd.ComputeString(System.Text.Encoding.ASCII.GetBytes(ascii));
            Assert.That(result, Is.EqualTo(expected));
        }

        // Regression for the [ThreadStatic] digest reuse: hashing several distinct inputs in a row on
        // the same thread must produce each input's correct digest (DoFinal must fully reset state
        // between calls). A reuse bug would surface as a later hash being contaminated by an earlier
        // BlockUpdate.
        [Test]
        public void Reused_digest_produces_correct_back_to_back_hashes()
        {
            (string ascii, string expected)[] cases =
            [
                ("abc", "0000000000000000000000008eb208f7e05d987a9b044a8e98c6b087f15a0bfc"),
                ("message digest", "0000000000000000000000005d0689ef49d2fae572b881b123a85ffa21595f36"),
                ("", "0000000000000000000000009c1185a5c5e9fc54612808977ee8f548b2258d31"),
                ("abc", "0000000000000000000000008eb208f7e05d987a9b044a8e98c6b087f15a0bfc"),
            ];

            foreach ((string ascii, string expected) in cases)
            {
                Assert.That(Ripemd.ComputeString(System.Text.Encoding.ASCII.GetBytes(ascii)), Is.EqualTo(expected));
            }
        }
    }
}
