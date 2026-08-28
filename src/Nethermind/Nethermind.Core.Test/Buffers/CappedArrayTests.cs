// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Buffers;
using NUnit.Framework;

namespace Nethermind.Core.Test.Buffers;

public class CappedArrayTests
{
    [Test]
    public void WhenGivenNullArray_IsNull_ShouldReturnTrue()
    {
        CappedArray<byte> array = new(null);
        Assert.That(array.IsNull, Is.True);
    }

    [Test]
    public void WhenGivenNullArray_AsSpan_ShouldReturnEmpty()
    {
        CappedArray<byte> array = new(null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(array.AsSpan().IsEmpty, Is.True);
            Assert.That(array.AsSpan().Length, Is.EqualTo(0));
            Assert.That(array.Length, Is.EqualTo(0));
        }
    }

    [Test]
    public void WhenGivenArray_AndLengthIsSame_ToArray_ShouldReturnSameArray()
    {
        int[] baseArray = Enumerable.Range(0, 10).ToArray();
        CappedArray<int> array = new(baseArray);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(array.IsUncapped, Is.True);
            Assert.That(array.IsNull, Is.False);
            Assert.That(array.IsNotNull, Is.True);
            Assert.That(array.Length, Is.EqualTo(10));
            Assert.That(array.ToArray(), Is.SameAs(baseArray));
        }
    }

    [Test]
    public void WhenGivenArray_AndLengthIsLess_ToArray_ShouldBeCapped()
    {
        int[] baseArray = Enumerable.Range(0, 10).ToArray();
        CappedArray<int> array = new(baseArray, 5);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(array.IsUncapped, Is.False);
            Assert.That(array.IsNull, Is.False);
            Assert.That(array.IsNotNull, Is.True);
            Assert.That(array.Length, Is.EqualTo(5));
            Assert.That(array.ToArray(), Is.EqualTo(baseArray[..5]));
            Assert.That(array.AsSpan().Length, Is.EqualTo(5));
            Assert.That(array.AsSpan().ToArray(), Is.EqualTo(baseArray[..5]));
        }
    }
}
