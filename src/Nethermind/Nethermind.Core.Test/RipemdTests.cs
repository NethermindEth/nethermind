// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Crypto;
using NUnit.Framework;
using TextEncoding = System.Text.Encoding;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class RipemdTests
    {
        [TestCase("", "0000000000000000000000009c1185a5c5e9fc54612808977ee8f548b2258d31")]
        [TestCase("abc", "0000000000000000000000008eb208f7e05d987a9b044a8e98c6b087f15a0bfc")]
        [TestCase("message digest", "0000000000000000000000005d0689ef49d2fae572b881b123a85ffa21595f36")]
        [TestCase("abcdefghijklmnopqrstuvwxyz", "000000000000000000000000f71c27109c692c1b56bbdceb5b9d2865b3708dbc")]
        public void Known_vectors(string ascii, string expected)
        {
            string result = Ripemd.ComputeString(TextEncoding.ASCII.GetBytes(ascii));
            Assert.That(result, Is.EqualTo(expected));
        }

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
                Assert.That(Ripemd.ComputeString(TextEncoding.ASCII.GetBytes(ascii)), Is.EqualTo(expected));
            }
        }
    }
}
