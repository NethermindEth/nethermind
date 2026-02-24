// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Test;

internal static class HsstTestUtil
{
    public delegate void BuildAction(ref HsstBuilder<SpanBufferWriter> builder);

    /// <summary>
    /// Helper for tests: Create builder, execute action, dispose and return result.
    /// </summary>
    public static byte[] BuildToArray(BuildAction buildAction, int maxLeafEntries = Hsst.Hsst.MaxLeafEntries, int extraSeparatorLength = 0)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(10 * 1024 * 1024);
        try
        {
            Span<byte> bufferSpan = buffer.AsSpan();
            SpanBufferWriter writer = new(bufferSpan);
            HsstBuilder<SpanBufferWriter> builder = new(ref writer, extraSeparatorLength);
            try
            {
                buildAction(ref builder);
                builder.Build(maxLeafEntries);
                int len = writer.Written;
                byte[] result = new byte[len];
                bufferSpan.Slice(0, len).CopyTo(result);
                return result;
            }
            finally
            {
                builder.Dispose();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
