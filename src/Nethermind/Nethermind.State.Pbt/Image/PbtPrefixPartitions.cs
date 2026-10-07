// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Pbt.Image;

/// <summary>Splits a keyspace into <paramref name="count"/> ascending ranges cut on its first two bytes.</summary>
internal readonly struct PbtPrefixPartitions(int count)
{
    /// <summary>Distinct values of the two leading key bytes the ranges are cut on, which bounds the range count.</summary>
    public const int PrefixSpace = 1 << 16;

    /// <summary>The two-byte prefix <paramref name="partition"/> of <paramref name="count"/> starts at, or
    /// <see cref="PrefixSpace"/> for the end of the last one.</summary>
    public static int Start(int partition, int count) => (int)((long)partition * PrefixSpace / count);

    /// <summary>Returns the bounds of <paramref name="partition"/>, the last one ending past every key.</summary>
    public (ValueHash256 Start, ValueHash256 End) Bounds(int partition)
    {
        ValueHash256 start = default;
        BinaryPrimitives.WriteUInt16BigEndian(start.BytesAsSpan, (ushort)Start(partition, count));

        if (partition == count - 1) return (start, ValueKeccak.MaxValue);

        ValueHash256 end = default;
        BinaryPrimitives.WriteUInt16BigEndian(end.BytesAsSpan, (ushort)Start(partition + 1, count));
        return (start, end);
    }
}
