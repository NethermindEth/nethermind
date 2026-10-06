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

    /// <summary>Returns the bounds of <paramref name="partition"/>, the last one ending past every key.</summary>
    public (ValueHash256 Start, ValueHash256 End) Bounds(int partition)
    {
        ValueHash256 start = default;
        BinaryPrimitives.WriteUInt16BigEndian(start.BytesAsSpan, (ushort)((long)partition * PrefixSpace / count));

        if (partition == count - 1) return (start, ValueKeccak.MaxValue);

        ValueHash256 end = default;
        BinaryPrimitives.WriteUInt16BigEndian(end.BytesAsSpan, (ushort)((long)(partition + 1) * PrefixSpace / count));
        return (start, end);
    }
}
