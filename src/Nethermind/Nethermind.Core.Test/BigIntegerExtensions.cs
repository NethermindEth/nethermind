// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using FluentAssertions;
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
            a.ToBigEndianByteArray(0).Should().BeEquivalentTo(new byte[0]);
        }
    }
}
