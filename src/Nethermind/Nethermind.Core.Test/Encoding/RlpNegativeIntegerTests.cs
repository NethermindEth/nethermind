// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

/// <summary>
/// Pins the fixed-width encoding of negative integers, which used to be produced by widening to
/// <see cref="BigInteger"/> and padding with 0xff.
/// </summary>
public class RlpNegativeIntegerTests
{
    [Test]
    public void Negative_int_encodes_as_four_byte_twos_complement([Values(-1, -2, -127, -128, -129, -255, -256, -65536, int.MinValue, int.MinValue + 1)] int value)
    {
        Rlp expected = Rlp.Encode(new BigInteger(value), 4);

        Assert.That(Rlp.Encode(value).Bytes, Is.EqualTo(expected.Bytes));
    }

    [Test]
    public void Negative_long_encodes_as_eight_byte_twos_complement([Values(-1L, -2L, -128L, -129L, -65536L, (long)int.MinValue, long.MinValue, long.MinValue + 1)] long value)
    {
        Rlp expected = Rlp.Encode(new BigInteger(value), 8);

        Assert.That(Rlp.Encode(value).Bytes, Is.EqualTo(expected.Bytes));
    }
}
