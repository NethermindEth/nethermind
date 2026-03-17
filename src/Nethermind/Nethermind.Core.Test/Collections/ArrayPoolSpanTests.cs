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
}