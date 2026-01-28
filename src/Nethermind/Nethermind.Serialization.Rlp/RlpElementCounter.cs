// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// A utility for counting all RLP elements in a stream without decoding them.
/// Used to prevent memory DOS attacks by validating total element count before allocation.
/// </summary>
public static class RlpElementCounter
{
    /// <summary>
    /// Counts all RLP elements (including nested) in the stream from current position.
    /// Throws RlpLimitException if the count exceeds maxAllowed.
    /// </summary>
    /// <param name="stream">The RLP stream to scan</param>
    /// <param name="maxAllowed">Maximum total elements allowed</param>
    /// <returns>Total count of all elements</returns>
    public static int CountElements(RlpStream stream, int maxAllowed)
    {
        int count = 0;
        int endPosition = stream.Length;
        CountRecursive(stream, endPosition, ref count, maxAllowed);
        return count;
    }

    /// <summary>
    /// Counts all RLP elements within a sequence boundary.
    /// </summary>
    public static int CountElementsInSequence(RlpStream stream, int maxAllowed)
    {
        int count = 0;
        int sequenceLength = stream.ReadSequenceLength();
        int endPosition = stream.Position + sequenceLength;
        CountRecursive(stream, endPosition, ref count, maxAllowed);
        return count;
    }

    private static void CountRecursive(RlpStream stream, int endPosition, ref int count, int maxAllowed)
    {
        while (stream.Position < endPosition)
        {
            byte prefix = stream.PeekByte();

            if (prefix >= 0xC0) // Sequence (list)
            {
                int seqLength = stream.ReadSequenceLength();
                int seqEnd = stream.Position + seqLength;

                // Count items in this sequence
                int items = stream.PeekNumberOfItemsRemaining(seqEnd);
                count += items;

                if (count > maxAllowed)
                {
                    ThrowLimitExceeded(count, maxAllowed);
                }

                // Recurse into nested sequences
                CountRecursive(stream, seqEnd, ref count, maxAllowed);
            }
            else
            {
                // Skip leaf item (string/bytes)
                stream.SkipItem();
            }
        }
    }

    [DoesNotReturn]
    private static void ThrowLimitExceeded(int count, int maxAllowed)
    {
        throw new RlpLimitException($"Total RLP elements {count} exceeds limit {maxAllowed}");
    }
}
