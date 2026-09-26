// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;

namespace Nethermind.Merge.Plugin.SszRest;

/// <summary>
/// Inline 2048-byte Blob Cell representation used by Engine API SSZ wire types.
/// Matches EIP-7594 <c>BYTES_PER_CELL = FIELD_ELEMENTS_PER_CELL(64) * BYTES_PER_FIELD_ELEMENT(32)</c>.
/// </summary>
[InlineArray(BlobCellLength)]
public struct SszBlobCell
{
    public const int BlobCellLength = 2048;

    private byte _element0;

    public static SszBlobCell FromSpan(ReadOnlySpan<byte> span)
    {
        if (span.Length != BlobCellLength)
        {
            throw new InvalidDataException($"{nameof(SszBlobCell)} expects input of length {BlobCellLength} and received {span.Length}");
        }

        SszBlobCell result = default;
        span.CopyTo(result);
        return result;
    }

    [UnscopedRef]
    public ReadOnlySpan<byte> AsSpan() => this;
}
