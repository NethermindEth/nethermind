// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Test;

internal static class HsstTestUtil
{
    public delegate void BuildAction(ref HsstBuilder<PooledByteBufferWriter.Writer> builder);

    /// <summary>
    /// Helper for tests: Create builder, execute action, dispose and return result.
    /// </summary>
    public static byte[] BuildToArray(BuildAction buildAction, int maxLeafEntries = Hsst.Hsst.MaxLeafEntries, int minSeparatorLength = 0)
    {
        using PooledByteBufferWriter pooled = new(10 * 1024 * 1024);
        HsstBuilder<PooledByteBufferWriter.Writer> builder = new(ref pooled.GetWriter(), minSeparatorLength);
        try
        {
            buildAction(ref builder);
            builder.Build(maxLeafEntries);
            return pooled.WrittenSpan.ToArray();
        }
        finally
        {
            builder.Dispose();
        }
    }
}
