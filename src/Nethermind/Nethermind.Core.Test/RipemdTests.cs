// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class RipemdTests
    {
        public const string RipemdOfEmptyString = "9c1185a5c5e9fc54612808977ee8f548b2258d31";

        [Test]
        public void Empty_byte_array()
        {
            string result = Ripemd.ComputeString(new byte[] { });
            Assert.That(result, Is.EqualTo(RipemdOfEmptyString));
        }
    }
}
