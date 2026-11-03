// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using Paprika.Data;

namespace Paprika.Tests.Data;

public class BitVectorTests
{
    [Test]
    public void Set_reset()
    {
        BitVector.Of1024 vector = new();

        for (int i = 0; i < BitVector.Of1024.Count; i++)
        {
            Assert.That(vector[i], Is.False);
            vector[i] = true;
            Assert.That(vector[i], Is.True);
            vector[i] = false;
            Assert.That(vector[i], Is.False);
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(64)]
    [TestCase(65)]
    [TestCase(BitVector.Of1024.Count - 1)]
    [TestCase(BitVector.Of1024.Count)]
    public void First_not_set(int set)
    {
        BitVector.Of1024 vector = new();

        for (int i = 0; i < set; i++)
        {
            vector[i] = true;
        }

        Assert.That(vector.FirstNotSet, Is.EqualTo((ushort)set));
    }

    [TestCase(1, true)]
    [TestCase(BitVector.Of1024.Count, false)]
    public void Any_not_set(int set, bool anyNotSet)
    {
        BitVector.Of1024 vector = new();

        for (int i = 0; i < set; i++)
        {
            vector[i] = true;
        }

        Assert.That(vector.HasEmptyBits, Is.EqualTo(anyNotSet));
    }

    [TestCase(0)]
    [TestCase(63)]
    [TestCase(64)]
    [TestCase(127)]
    [TestCase(128)]
    [TestCase(BitVector.Of256.Count - 2)]
    [TestCase(BitVector.Of256.Count - 1)]
    public void HighestSmallerOrEqualThan(int set)
    {
        BitVector.Of256 vector = new()
        {
            [set] = true
        };

        for (int i = 0; i < BitVector.Of256.Count; i++)
        {
            int expected = i < set ? BitVector.NotFound : set;

            Assert.That(vector.HighestSmallerOrEqualThan(i), Is.EqualTo(expected));
        }
    }
}
