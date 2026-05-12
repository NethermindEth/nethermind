// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Test;

internal static class HsstTestUtil
{
    public delegate void BuildAction(ref HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder);

    /// <summary>
    /// Helper for tests: Create builder, execute action, dispose and return result.
    /// </summary>
    /// <summary>
    /// Test helper: defaults <paramref name="keyLength"/> to -1 ("infer from first key"). Production code
    /// must pass an explicit key length to <see cref="HsstBTreeBuilder{TWriter,TReader,TPin}"/>; tests using
    /// this helper rely on the builder picking up the length from the first <see cref="HsstBTreeBuilder{TWriter,TReader,TPin}.Add"/>
    /// call and validating that every subsequent key matches.
    /// </summary>
    public static byte[] BuildToArray(BuildAction buildAction, int keyLength = -1, int maxLeafEntries = HsstBTreeOptions.DefaultMaxLeafEntries, int minSeparatorLength = 0)
    {
        using PooledByteBufferWriter pooled = new(10 * 1024 * 1024);
        HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder = new(ref pooled.GetWriter(), keyLength, new HsstBTreeOptions
        {
            MinSeparatorLength = minSeparatorLength,
            MaxLeafEntries = maxLeafEntries,
        });
        try
        {
            buildAction(ref builder);
            builder.Build();
            return pooled.WrittenSpan.ToArray();
        }
        finally
        {
            builder.Dispose();
        }
    }
}
