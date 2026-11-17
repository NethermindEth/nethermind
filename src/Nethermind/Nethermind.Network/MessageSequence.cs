// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network;

internal static class MessageSequence
{
    private const int MaxShortSequenceLength = 56;

    public static int GetContentLength(int sequenceLength)
    {
        if (sequenceLength is > 0 and <= MaxShortSequenceLength)
        {
            return sequenceLength - 1;
        }

        for (int lengthOfLength = 1; lengthOfLength <= sizeof(int); lengthOfLength++)
        {
            int contentLength = sequenceLength - 1 - lengthOfLength;
            if (contentLength >= MaxShortSequenceLength && Rlp.LengthOfLength(contentLength) == lengthOfLength)
            {
                return contentLength;
            }
        }

        ThrowInvalidSequenceLength(sequenceLength);
        return 0;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowInvalidSequenceLength(int length)
            => throw new ArgumentException($"{length} bytes cannot hold exactly one RLP sequence.", nameof(sequenceLength));
    }
}
