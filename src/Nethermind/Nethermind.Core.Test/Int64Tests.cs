// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class Int64Tests
    {
        [Test]
        public void ToLongFromBytes()
        {
            byte[] bytes = Bytes.FromHexString("7fffffffffffffff");
            long number = bytes.ToLongFromBigEndianByteArrayWithoutLeadingZeros();
            Assert.That(number, Is.EqualTo(long.MaxValue));
        }
    }
}
