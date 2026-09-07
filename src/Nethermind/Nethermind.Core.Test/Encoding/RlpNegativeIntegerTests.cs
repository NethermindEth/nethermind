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
    [TestCase(-1)]
    [TestCase(-2)]
    [TestCase(-127)]
    [TestCase(-128)]
    [TestCase(-129)]
    [TestCase(-255)]
    [TestCase(-256)]
    [TestCase(-65536)]
    [TestCase(int.MinValue)]
    [TestCase(int.MinValue + 1)]
    public void Negative_int_encodes_as_four_byte_twos_complement(int value)
    {
        Rlp expected = Rlp.Encode(new BigInteger(value), 4);

        Assert.That(Rlp.Encode(value).Bytes, Is.EqualTo(expected.Bytes));
    }

    [TestCase(-1L)]
    [TestCase(-2L)]
    [TestCase(-128L)]
    [TestCase(-129L)]
    [TestCase(-65536L)]
    [TestCase((long)int.MinValue)]
    [TestCase(long.MinValue)]
    [TestCase(long.MinValue + 1)]
    public void Negative_long_encodes_as_eight_byte_twos_complement(long value)
    {
        Rlp expected = Rlp.Encode(new BigInteger(value), 8);

        Assert.That(Rlp.Encode(value).Bytes, Is.EqualTo(expected.Bytes));
    }
}
