// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using FluentAssertions;
using Nethermind.Core.Buffers;
using NUnit.Framework;

namespace Nethermind.Core.Test.Buffers;

public class CappedArrayTests
{
    [Test]
    public void WhenGivenNullArray_IsNull_ShouldReturnTrue()
    {
        CappedArray<byte> array = new(null);
        array.IsNull.Should().BeTrue();
    }

    [Test]
    public void WhenGivenNullArray_AsSpan_ShouldReturnEmpty()
    {
        CappedArray<byte> array = new(null);
        array.AsSpan().IsEmpty.Should().BeTrue();
        array.AsSpan().Length.Should().Be(0);
        array.Length.Should().Be(0);
    }

    [Test]
    public void WhenGivenArray_AndLengthIsSame_ToArray_ShouldReturnSameArray()
    {
        int[] baseArray = Enumerable.Range(0, 10).ToArray();
        CappedArray<int> array = new(baseArray);
        array.IsUncapped.Should().BeTrue();
        array.IsNull.Should().BeFalse();
        array.IsNotNull.Should().BeTrue();
        array.Length.Should().Be(10);
        array.ToArray().Should().BeSameAs(baseArray);
    }

    [Test]
    public void WhenGivenArray_AndLengthIsLess_ToArray_ShouldBeCapped()
    {
        int[] baseArray = Enumerable.Range(0, 10).ToArray();
        CappedArray<int> array = new(baseArray, 5);
        array.IsUncapped.Should().BeFalse();
        array.IsNull.Should().BeFalse();
        array.IsNotNull.Should().BeTrue();
        array.Length.Should().Be(5);
        array.ToArray().Should().BeEquivalentTo(baseArray[..5]);
        array.AsSpan().Length.Should().Be(5);
        array.AsSpan().ToArray().Should().BeEquivalentTo(baseArray[..5]);
    }
}
