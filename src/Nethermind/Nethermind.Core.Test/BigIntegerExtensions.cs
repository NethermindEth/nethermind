// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    public class BigIntegerExtensions
    {
        [Test]
        public void Test()
        {
            BigInteger a = BigInteger.One;
            Assert.That(a.ToBigEndianByteArray(0), Is.EqualTo(Array.Empty<byte>()));
        }
    }
}
