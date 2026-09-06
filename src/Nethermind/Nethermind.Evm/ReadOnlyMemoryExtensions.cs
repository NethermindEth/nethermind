// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;

namespace Nethermind.Evm
{
    public static class ReadOnlyMemoryExtensions
    {
        public static bool StartsWith(this ReadOnlyMemory<byte> inputData, byte startingByte)
        {
            ReadOnlySpan<byte> span = inputData.Span;
            return span.Length > 0 && span[0] == startingByte;
        }

        public static bool StartsWith(this ReadOnlyMemory<byte> inputData, Span<byte> startingBytes) => inputData.Span.StartsWith(startingBytes);

        public static byte ByteAt(this ReadOnlyMemory<byte> inputData, int index) => inputData.Length > index ? inputData.Span[index] : (byte)0;

        // Forwards the backing array when it spans a whole array, copying otherwise. The result may be shared
        // (e.g. a precompile's static output), so callers must treat it as read-only.
        public static byte[] AsReadOnlyArray(this ReadOnlyMemory<byte> memory) =>
            memory.IsEmpty ? []
            : MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment)
              && segment.Offset == 0 && segment.Count == segment.Array!.Length
                ? segment.Array!
                : memory.ToArray();
    }
}
