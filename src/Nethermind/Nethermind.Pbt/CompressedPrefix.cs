// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;

namespace Nethermind.Pbt;

/// <summary>A borrowed view of a compressed prefix's big-endian bit count and canonical bytes.</summary>
/// <remarks>The backing memory must remain alive and unchanged while the view is used. The default value represents an empty prefix.</remarks>
public readonly ref struct CompressedPrefix
{
    private readonly ReadOnlySpan<byte> _encoding;

    private CompressedPrefix(ReadOnlySpan<byte> encoding) => _encoding = encoding;

    /// <summary>Gets the number of meaningful prefix bits.</summary>
    public int BitCount => _encoding.IsEmpty ? 0 : BinaryPrimitives.ReadUInt16BigEndian(_encoding);

    /// <summary>Gets the borrowed, MSB-first prefix bytes without the count header.</summary>
    public ReadOnlySpan<byte> Bytes => _encoding.IsEmpty ? [] : _encoding[sizeof(ushort)..];

    internal static CompressedPrefix FromValidated(ReadOnlySpan<byte> encoding) => new(encoding);
}
