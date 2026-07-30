// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class UInt64Tests
    {
        [TestCase(0UL, 0UL, 0UL)]
        [TestCase(1UL, 2UL, 3UL)]
        [TestCase(ulong.MaxValue, 0UL, ulong.MaxValue)]
        [TestCase(ulong.MaxValue, 1UL, ulong.MaxValue)]
        [TestCase(ulong.MaxValue - 1, 1UL, ulong.MaxValue)]
        [TestCase(ulong.MaxValue - 1, 2UL, ulong.MaxValue)]
        public void SaturatingAdd(ulong a, ulong b, ulong expected) =>
            Assert.That(a.SaturatingAdd(b), Is.EqualTo(expected));

        [TestCase("7fffffffffffffff", (ulong)long.MaxValue)]
        [TestCase("ffffffffffffffff", ulong.MaxValue)]
        [TestCase("0000", (ulong)0)]
        [TestCase("0001234", (ulong)0x1234)]
        [TestCase("1234", (ulong)0x1234)]
        [TestCase("1", (ulong)1)]
        [TestCase("10", (ulong)16)]
        public void ToLongFromBytes(string hexBytes, ulong expectedValue)
        {
            byte[] bytes = Bytes.FromHexString(hexBytes);
            ulong number = bytes.ToULongFromBigEndianByteArrayWithoutLeadingZeros();
            Assert.That(number, Is.EqualTo(expectedValue));
        }
    }
}
