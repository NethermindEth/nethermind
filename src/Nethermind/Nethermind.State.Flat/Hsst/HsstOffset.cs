// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Shared offset-encoding policy used by the packed-array-style HSST formats
/// (<see cref="IndexType.PackedArray"/> uses a fixed value size and does not
/// participate; <see cref="IndexType.DenseByteIndex"/> picks its on-disk end-offset
/// width from the running <c>valuesTotal</c> via <see cref="ChooseOffsetSize"/>).
/// </summary>
internal static class HsstOffset
{
    /// <summary>Maximum addressable values-region size (256 TiB − 1, the limit of 6-byte LE).</summary>
    public const long MaxValuesTotal = (1L << 48) - 1;

    /// <summary>
    /// Pick the smallest <c>OffsetSize ∈ {1,2,4,6}</c> that can represent every
    /// cumulative end offset up to <paramref name="valuesTotal"/>. Throws when the
    /// payload would exceed the 256 TiB ceiling encodable by a 6-byte LE offset.
    /// </summary>
    public static int ChooseOffsetSize(long valuesTotal)
    {
        if (valuesTotal <= byte.MaxValue) return 1;
        if (valuesTotal <= ushort.MaxValue) return 2;
        if (valuesTotal <= uint.MaxValue) return 4;
        if (valuesTotal <= MaxValuesTotal) return 6;
        throw new InvalidOperationException("HSST values-region size exceeds 256 TiB.");
    }

    /// <summary>Validate an OffsetSize byte read from a trailer.</summary>
    public static bool IsValidOffsetSize(int offsetSize)
        => offsetSize == 1 || offsetSize == 2 || offsetSize == 4 || offsetSize == 6;
}
