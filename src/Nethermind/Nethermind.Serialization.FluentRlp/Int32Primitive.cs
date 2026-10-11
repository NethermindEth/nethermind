// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;

namespace Nethermind.Serialization.FluentRlp;

internal static class Int32Primitive
{
    /// <summary>
    /// Reads a <see cref="int" /> from the beginning of a read-only span of bytes, as big endian.
    /// </summary>
    /// <param name="source">The read-only span to read.</param>
    /// <returns>The big endian value.</returns>
    /// <remarks>The span is padded with leading `0`s as needed.</remarks>
    public static int Read(ReadOnlySpan<byte> source)
    {
        Span<byte> buffer = stackalloc byte[sizeof(Int32)];
        source.CopyTo(buffer[^source.Length..]);
        return BinaryPrimitives.ReadInt32BigEndian(buffer);
    }
}
