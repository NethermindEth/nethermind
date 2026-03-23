// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections;

public class ArrayPoolSpanTests
{
    [TestCase(8, 8, Description = "At index == Length")]
    [TestCase(4, 5, Description = "Beyond Length")]
    public void Indexer_out_of_bounds_should_throw(int length, int index)
    {
        using ArrayPoolSpan<int> span = new(length);
        span.Invoking(s => { int _ = s[index]; }).Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestCase(4, -1)]
    [TestCase(8, -5)]
    public void Indexer_negative_should_throw(int length, int index)
    {
        using ArrayPoolSpan<int> span = new(length);
        span.Invoking(s => { int _ = s[index]; }).Should().Throw<IndexOutOfRangeException>();
    }

    [Test]
    public void Indexer_within_bounds_should_round_trip()
    {
        using ArrayPoolSpan<int> span = new(4);
        span[0] = 10;
        span[3] = 40;
        span[0].Should().Be(10);
        span[3].Should().Be(40);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(7)]
    [TestCase(16)]
    public void Length_should_return_requested_size(int length)
    {
        using ArrayPoolSpan<int> span = new(length);
        span.Length.Should().Be(length);
    }

    [Test]
    public void Implicit_span_conversion_should_respect_length()
    {
        using ArrayPoolSpan<int> span = new(4);
        for (int i = 0; i < 4; i++) span[i] = i;

        Span<int> s = span;
        s.Length.Should().Be(4);
        s[0].Should().Be(0);
        s[3].Should().Be(3);
    }

    [Test]
    public void Implicit_readonly_span_conversion_should_respect_length()
    {
        using ArrayPoolSpan<int> span = new(4);
        for (int i = 0; i < 4; i++) span[i] = i;

        ReadOnlySpan<int> s = span;
        s.Length.Should().Be(4);
        s[0].Should().Be(0);
        s[3].Should().Be(3);
    }

    [Test]
    public void Enumeration_should_yield_exactly_length_elements()
    {
        using ArrayPoolSpan<int> span = new(5);
        for (int i = 0; i < 5; i++) span[i] = i * 10;

        int count = 0;
        foreach (int val in span)
        {
            val.Should().Be(count * 10);
            count++;
        }
        count.Should().Be(5);
    }

    [Test]
    public void Slice_respects_logical_length()
    {
        using ArrayPoolSpan<int> span = new(5);
        for (int i = 0; i < 5; i++) span[i] = i;

        Span<int> slice = span.Slice(1, 3);
        slice.Length.Should().Be(3);
        slice[0].Should().Be(1);
        slice[2].Should().Be(3);
    }

    [Test]
    public void Slice_throws_when_exceeding_logical_length()
    {
        using ArrayPoolSpan<int> span = new(5);

        // Start + length exceeds logical length (5), even though rented array may be larger
        Assert.Throws<ArgumentOutOfRangeException>(() => span.Slice(3, 5));
    }

    [Test]
    public void Slice_at_boundary_succeeds()
    {
        using ArrayPoolSpan<int> span = new(5);

        // Exactly at the boundary should work
        Span<int> slice = span.Slice(0, 5);
        slice.Length.Should().Be(5);
    }
}
