// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Buffers;
using NUnit.Framework;

namespace Nethermind.Core.Test.Buffers;

public class SpanSourceTests
{
    [Test]
    public void Null()
    {
        SpanSource source = SpanSource.Null;

        source.IsNull.Should().Be(true);
        source.IsNotNull.Should().Be(false);
        source.IsNotNullOrEmpty.Should().Be(false);
        source.Span.IsEmpty.Should().Be(true, "Should follow semantics of ((byte[])null).AsSpan()");
    }

    [Test]
    public void Empty()
    {
        SpanSource source = SpanSource.Empty;

        source.IsNull.Should().Be(false);
        source.IsNotNull.Should().Be(true);
        source.IsNotNullOrEmpty.Should().Be(false);
        source.Span.IsEmpty.Should().Be(true);
    }

    [Test]
    public void Array()
    {
        byte[] array = [1];
        SpanSource source = new SpanSource(array);

        source.IsNull.Should().Be(false);
        source.IsNotNull.Should().Be(true);
        source.IsNotNullOrEmpty.Should().Be(true);

        const bool equality = true;

        source.Span.SequenceEqual(array).Should().Be(equality);

        int commonPrefixLength = array.Length;

        source.Span.CommonPrefixLength(array).Should().Be(commonPrefixLength);
    }
}
