// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.State.Flat.BSearchIndex;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Test;

internal static class HsstTestUtil
{
    public delegate void BuildAction(ref HsstBuilder<PooledByteBufferWriter.Writer> builder);

    /// <summary>
    /// Helper for tests: Create builder, execute action, dispose and return result.
    /// </summary>
    public static byte[] BuildToArray(BuildAction buildAction, int maxLeafEntries = HsstBTreeOptions.DefaultMaxLeafEntries, int minSeparatorLength = 0, bool inlineValues = false, bool useHashIndex = false, double hashIndexTargetUtilization = 0.75, HashProbeMode leafHashProbeMode = HashProbeMode.None)
    {
        using PooledByteBufferWriter pooled = new(10 * 1024 * 1024);
        HsstBuilder<PooledByteBufferWriter.Writer> builder = new(ref pooled.GetWriter(), new HsstBTreeOptions
        {
            MinSeparatorLength = minSeparatorLength,
            InlineValues = inlineValues,
            UseHashIndex = useHashIndex,
            HashIndexTargetUtilization = hashIndexTargetUtilization,
            LeafHashProbeMode = leafHashProbeMode,
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
