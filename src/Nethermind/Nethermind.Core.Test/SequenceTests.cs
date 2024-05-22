// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using FluentAssertions;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class SequenceTests
{
    [Test]
    public void Sequence_whitespace_slice()
    {
        var end = new TestReadOnlySequenceSegment("\r\nabc"u8.ToArray(), 2);
        var start = new TestReadOnlySequenceSegment("  "u8.ToArray(), 0, end);
        ReadOnlySequence<byte> sequence = new ReadOnlySequence<byte>(start, 0, end, 5);

        sequence.TrimStart().ToArray().Should().Equal("abc"u8.ToArray());
    }

    private class TestReadOnlySequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public TestReadOnlySequenceSegment(Memory<byte> memory, long runningIndex = 0, ReadOnlySequenceSegment<byte>? next = null)
        {
            Memory = memory;
            Next = next;
            RunningIndex = runningIndex;
        }
    }
}
