// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Hsst.BTree;

namespace Nethermind.State.Flat.Test.Hsst;

internal static class HsstTestUtil
{
    public delegate void BuildAction(ref HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder);

    /// <summary>
    /// Test helper: create a builder, execute <paramref name="buildAction"/>, dispose, and return the
    /// built HSST bytes. Defaults <paramref name="keyLength"/> to -1 ("infer from first key") — production
    /// code must pass an explicit key length to <see cref="HsstBTreeBuilder{TWriter,TReader,TPin}"/>; tests
    /// using this helper rely on the builder picking up the length from the first
    /// <see cref="HsstBTreeBuilder{TWriter,TReader,TPin}.Add"/> call and validating that every subsequent
    /// key matches.
    /// </summary>
    public static byte[] BuildToArray(BuildAction buildAction, int keyLength = -1, bool keyFirst = false)
    {
        using PooledByteBufferWriter pooled = new(10 * 1024 * 1024);
        using HsstBTreeBuilderBuffersContainer buffers = new();
        HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder = new(ref pooled.GetWriter(), ref buffers.Buffers, keyLength, keyFirst: keyFirst);
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
    public static bool TryGet(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> key, out byte[] value) =>
        TryGetCore(data, key, twoByteSlot: false, floor: false, out value);

    /// <summary>Test helper: floor-seek variant of <see cref="TryGet(ReadOnlySpan{byte},ReadOnlySpan{byte},out byte[])"/>.</summary>
    public static bool TryGetFloor(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> key, out byte[] value) =>
        TryGetCore(data, key, twoByteSlot: false, floor: true, out value);

    /// <summary>
    /// Test helper: front-dispatch lookup over a keys-first two-byte-slot HSST blob
    /// (<see cref="IndexType.TwoByteSlotValue"/> / <see cref="IndexType.TwoByteSlotValueLarge"/>),
    /// whose IndexType byte leads the blob at byte 0.
    /// </summary>
    public static bool TryGetTwoByteSlot(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> key, out byte[] value) =>
        TryGetCore(data, key, twoByteSlot: true, floor: false, out value);

    /// <summary>Test helper: floor-seek variant of <see cref="TryGetTwoByteSlot"/>.</summary>
    public static bool TryGetTwoByteSlotFloor(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> key, out byte[] value) =>
        TryGetCore(data, key, twoByteSlot: true, floor: true, out value);

    private static bool TryGetCore(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> key, bool twoByteSlot, bool floor, out byte[] value)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        bool found = (twoByteSlot, floor) switch
        {
            (false, false) => r.TrySeek(key, out _),
            (false, true) => r.TrySeekFloor(key, out _),
            (true, false) => r.TrySeekTwoByteSlot(key, out _),
            (true, true) => r.TrySeekTwoByteSlotFloor(key, out _),
        };
        if (!found) { value = []; return false; }
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
