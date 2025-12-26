// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm
{
    public static class ReadOnlyMemoryExtensions
    {
        public static bool StartsWith(this ReadOnlyMemory<byte> inputData, byte startingByte)
        {
            ReadOnlySpan<byte> span = inputData.Span;
            return span.Length > 0 && span[0] == startingByte;
        }

        public static bool StartsWith(this ReadOnlyMemory<byte> inputData, Span<byte> startingBytes)
        {
            return inputData.Span.StartsWith(startingBytes);
        }

        public static byte ByteAt(this ReadOnlyMemory<byte> inputData, int index)
        {
            return inputData.Length > index ? inputData.Span[index] : (byte)0;
        }
    }
}
