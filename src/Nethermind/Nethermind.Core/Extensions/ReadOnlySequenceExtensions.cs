// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;

namespace Nethermind.Core.Extensions;

public static class ReadOnlySequenceExtensions
{
    private static readonly byte[] WhitespaceChars = " \t\r\n"u8.ToArray();

    public static ReadOnlySequence<byte> TrimStart(this ReadOnlySequence<byte> sequence, byte[]? chars = null)
    {
        ReadOnlySpan<byte> charsSpan = chars ?? WhitespaceChars;
        SequencePosition start = sequence.Start;

        foreach (ReadOnlyMemory<byte> memory in sequence)
        {
            ReadOnlySpan<byte> span = memory.Span;
            int index = span.IndexOfAnyExcept(charsSpan);

            if (index == -1)
            {
                // The entire segment is trimmed chars, advance past it
                start = sequence.GetPosition(span.Length, start);
            }
            else if (index > 0)
            {
                // Found non-trimmed char partway through the segment
                start = sequence.GetPosition(index, start);
                return sequence.Slice(start);
            }
            else
            {
                // First char is non-trimmed, we're done
                return sequence.Slice(start);
            }
        }

        // The entire sequence was trimmed chars
        return sequence.Slice(sequence.End);
    }
    }
}
