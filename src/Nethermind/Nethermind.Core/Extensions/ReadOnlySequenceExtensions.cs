// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;

namespace Nethermind.Core.Extensions;

public static class ReadOnlySequenceExtensions
{
    private static readonly byte[] WhitespaceChars = " \t\r\n"u8.ToArray();

    public static ReadOnlySequence<byte> SliceLeadingWhitespace(this ReadOnlySequence<byte> sequence)
    {
        SequencePosition position = sequence.Start;
        while (sequence.TryGet(ref position, out ReadOnlyMemory<byte> memory))
        {
            ReadOnlySpan<byte> span = memory.Span;
            int index = span.IndexOfAnyExcept(WhitespaceChars);

            if (index != 0)
            {
                // if index == -1, then the whole span is whitespace
                sequence = sequence.Slice(index != -1 ? index : span.Length);
            }
            else
            {
                return sequence;
            }
        }

        return sequence;
    }
}
