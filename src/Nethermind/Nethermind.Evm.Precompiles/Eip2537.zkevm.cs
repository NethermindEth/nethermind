// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm.Precompiles;

internal static partial class Eip2537
{
    /// <param name="source">Must have <see cref="LenG1Trimmed"/> bytes length.</param>
    /// <param name="destination">Must be zero-initialized.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EncodeG1(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        source[..LenFpTrimmed].CopyTo(destination[LenFpPad..LenFp]);
        source[LenFpTrimmed..].CopyTo(destination[(LenFp + LenFpPad)..(2 * LenFp)]);
    }

    /// <param name="source">Must have <see cref="LenG2Trimmed"/> bytes length.</param>
    /// <param name="destination">Must be zero-initialized.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EncodeG2(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        source[..LenFpTrimmed].CopyTo(destination[LenFpPad..LenFp]);
        source[LenFpTrimmed..(2 * LenFpTrimmed)].CopyTo(destination[(LenFp + LenFpPad)..(2 * LenFp)]);
        source[(2 * LenFpTrimmed)..(3 * LenFpTrimmed)].CopyTo(destination[(2 * LenFp + LenFpPad)..(3 * LenFp)]);
        source[(3 * LenFpTrimmed)..].CopyTo(destination[(3 * LenFp + LenFpPad)..(4 * LenFp)]);
    }

    /// <summary>
    /// Unpads a field element into its trimmed form, rejecting non-canonical input: the leading pad must
    /// be zero and the value must be less than the base field modulus.
    /// </summary>
    /// <remarks>
    /// EIP-2537 requires canonical Fp coordinates; otherwise the accelerator silently reduces mod p,
    /// accepting invalid input.
    /// </remarks>
    /// <param name="source">Must have <see cref="LenFp"/> bytes length.</param>
    /// <param name="destination">Must have <see cref="LenFpTrimmed"/> bytes length.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryDecodeFp(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        ReadOnlySpan<byte> value = source[LenFpPad..];

        if (source[..LenFpPad].ContainsAnyExcept((byte)0) || value.SequenceCompareTo(_baseFieldOrder) >= 0)
            return false;

        value.CopyTo(destination);

        return true;
    }

    /// <param name="source">Must have <see cref="LenFp"/> * 2 bytes length.</param>
    /// <param name="destination">Must have <see cref="LenFpTrimmed"/> * 2 bytes length.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryDecodeFp2(ReadOnlySpan<byte> source, Span<byte> destination)
        => TryDecodeFp(source[..LenFp], destination)
        && TryDecodeFp(source[LenFp..], destination[LenFpTrimmed..]);

    /// <param name="source">Must have <see cref="LenFp"/> * 2 bytes length.</param>
    /// <param name="destination">Must have <see cref="LenFpTrimmed"/> * 2 bytes length.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryDecodeG1(ReadOnlySpan<byte> source, Span<byte> destination)
        => TryDecodeFp(source[..LenFp], destination)
        && TryDecodeFp(source[LenFp..], destination[LenFpTrimmed..]);

    /// <param name="source">Must have <see cref="LenFp"/> * 4 bytes length.</param>
    /// <param name="destination">Must have <see cref="LenFpTrimmed"/> * 4 bytes length.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryDecodeG2(ReadOnlySpan<byte> source, Span<byte> destination)
        => TryDecodeFp(source[..LenFp], destination)
        && TryDecodeFp(source[LenFp..(2 * LenFp)], destination[LenFpTrimmed..(2 * LenFpTrimmed)])
        && TryDecodeFp(source[(2 * LenFp)..(3 * LenFp)], destination[(2 * LenFpTrimmed)..(3 * LenFpTrimmed)])
        && TryDecodeFp(source[(3 * LenFp)..], destination[(3 * LenFpTrimmed)..]);
}
