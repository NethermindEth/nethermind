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
    public static byte[] BuildToArray(BuildAction buildAction, int keyLength = -1, int maxLeafEntries = HsstBTreeOptions.DefaultMaxLeafEntries, bool keyFirst = false, int commonKeyPrefixLength = 0)
    {
        using PooledByteBufferWriter pooled = new(10 * 1024 * 1024);
        HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder = new(ref pooled.GetWriter(), keyLength, new HsstBTreeOptions
        {
            MaxLeafEntries = maxLeafEntries,
        }, keyFirst: keyFirst, commonKeyPrefixLength: commonKeyPrefixLength);
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

    /// <summary>Test helper: dispatcher-style lookup over an HSST byte blob via <see cref="HsstReader{TReader,TPin}"/>.</summary>
    public static bool TryGet(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> key, out byte[] value)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeek(key, out _)) { value = []; return false; }
        Bound b = r.GetBound();
        value = b.Length == 0 ? [] : data.Slice((int)b.Offset, (int)b.Length).ToArray();
        return true;
    }

    /// <summary>Test helper: floor-seek variant of <see cref="TryGet(ReadOnlySpan{byte},ReadOnlySpan{byte},out byte[])"/>.</summary>
    public static bool TryGetFloor(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> key, out byte[] value)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeekFloor(key, out _)) { value = []; return false; }
        Bound b = r.GetBound();
        value = b.Length == 0 ? [] : data.Slice((int)b.Offset, (int)b.Length).ToArray();
        return true;
    }

    /// <summary>Test helper: single-byte-key overload for the dense-byte-index format.</summary>
    public static bool TryGet(ReadOnlySpan<byte> data, byte key, out byte[] value) =>
        TryGet(data, [key], out value);

    /// <summary>Test helper: floor-seek single-byte-key overload for the dense-byte-index format.</summary>
    public static bool TryGetFloor(ReadOnlySpan<byte> data, byte key, out byte[] value) =>
        TryGetFloor(data, [key], out value);
}
