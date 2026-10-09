// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;

namespace Nethermind.Evm.Precompiles;

internal static partial class Eip2537
{
    /// <summary>The bit SP1's layout sets in a point's first byte to denote the point at infinity.</summary>
    internal const byte Sp1InfinityBit = 0x40;

    /// <param name="source">Must have <see cref="LenG1Trimmed"/> bytes length.</param>
    /// <param name="destination">Must be zero-initialized.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EncodeG1(ReadOnlySpan<byte> source, Span<byte> destination)
        => EncodeG1<Sp1Bls12381LayoutFlag>(source, destination);

    /// <inheritdoc cref="EncodeG1(ReadOnlySpan{byte}, Span{byte})"/>
    /// <typeparam name="TSp1Layout">Whether <paramref name="source"/> is in SP1's layout.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EncodeG1<TSp1Layout>(ReadOnlySpan<byte> source, Span<byte> destination)
        where TSp1Layout : struct, IFlag
    {
        if (TSp1Layout.IsActive && (source[0] & Sp1InfinityBit) != 0)
            return;

        source[..LenFpTrimmed].CopyTo(destination[LenFpPad..LenFp]);
        source[LenFpTrimmed..].CopyTo(destination[(LenFp + LenFpPad)..(2 * LenFp)]);
    }

    /// <param name="source">Must have <see cref="LenG2Trimmed"/> bytes length.</param>
    /// <param name="destination">Must be zero-initialized.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EncodeG2(ReadOnlySpan<byte> source, Span<byte> destination)
        => EncodeG2<Sp1Bls12381LayoutFlag>(source, destination);

    /// <inheritdoc cref="EncodeG2(ReadOnlySpan{byte}, Span{byte})"/>
    /// <typeparam name="TSp1Layout">Whether <paramref name="source"/> is in SP1's layout.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EncodeG2<TSp1Layout>(ReadOnlySpan<byte> source, Span<byte> destination)
        where TSp1Layout : struct, IFlag
    {
        if (TSp1Layout.IsActive && (source[0] & Sp1InfinityBit) != 0)
            return;

        EncodeFp2<TSp1Layout>(source[..(2 * LenFpTrimmed)], destination[..(2 * LenFp)]);
        EncodeFp2<TSp1Layout>(source[(2 * LenFpTrimmed)..], destination[(2 * LenFp)..]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EncodeFp2<TSp1Layout>(ReadOnlySpan<byte> source, Span<byte> destination)
        where TSp1Layout : struct, IFlag
    {
        int c0 = TSp1Layout.IsActive ? LenFpTrimmed : 0;

        source.Slice(c0, LenFpTrimmed).CopyTo(destination[LenFpPad..LenFp]);
        source.Slice(LenFpTrimmed - c0, LenFpTrimmed).CopyTo(destination[(LenFp + LenFpPad)..(2 * LenFp)]);
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
        => TryDecodeFp2<Sp1Bls12381LayoutFlag>(source, destination);

    /// <inheritdoc cref="TryDecodeFp2(ReadOnlySpan{byte}, Span{byte})"/>
    /// <typeparam name="TSp1Layout">Whether to write <paramref name="destination"/> in SP1's layout.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryDecodeFp2<TSp1Layout>(ReadOnlySpan<byte> source, Span<byte> destination)
        where TSp1Layout : struct, IFlag
    {
        int c0 = TSp1Layout.IsActive ? LenFpTrimmed : 0;

        return TryDecodeFp(source[..LenFp], destination.Slice(c0, LenFpTrimmed))
            && TryDecodeFp(source[LenFp..], destination.Slice(LenFpTrimmed - c0, LenFpTrimmed));
    }

    /// <param name="source">Must have <see cref="LenFp"/> * 2 bytes length.</param>
    /// <param name="destination">Must have <see cref="LenFpTrimmed"/> * 2 bytes length.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryDecodeG1(ReadOnlySpan<byte> source, Span<byte> destination)
        => TryDecodeG1<Sp1Bls12381LayoutFlag>(source, destination);

    /// <inheritdoc cref="TryDecodeG1(ReadOnlySpan{byte}, Span{byte})"/>
    /// <typeparam name="TSp1Layout">Whether to write <paramref name="destination"/> in SP1's layout.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryDecodeG1<TSp1Layout>(ReadOnlySpan<byte> source, Span<byte> destination)
        where TSp1Layout : struct, IFlag
    {
        if (!TryDecodeFp(source[..LenFp], destination) || !TryDecodeFp(source[LenFp..], destination[LenFpTrimmed..]))
            return false;

        if (TSp1Layout.IsActive)
            MarkSp1Infinity(destination);

        return true;
    }

    /// <param name="source">Must have <see cref="LenFp"/> * 4 bytes length.</param>
    /// <param name="destination">Must have <see cref="LenFpTrimmed"/> * 4 bytes length.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryDecodeG2(ReadOnlySpan<byte> source, Span<byte> destination)
        => TryDecodeG2<Sp1Bls12381LayoutFlag>(source, destination);

    /// <inheritdoc cref="TryDecodeG2(ReadOnlySpan{byte}, Span{byte})"/>
    /// <typeparam name="TSp1Layout">Whether to write <paramref name="destination"/> in SP1's layout.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryDecodeG2<TSp1Layout>(ReadOnlySpan<byte> source, Span<byte> destination)
        where TSp1Layout : struct, IFlag
    {
        if (!TryDecodeFp2<TSp1Layout>(source[..(2 * LenFp)], destination[..(2 * LenFpTrimmed)]) ||
            !TryDecodeFp2<TSp1Layout>(source[(2 * LenFp)..], destination[(2 * LenFpTrimmed)..]))
            return false;

        if (TSp1Layout.IsActive)
            MarkSp1Infinity(destination);

        return true;
    }

    /// <remarks>(0, 0) is on neither curve, so all zeros can only be EIP-2537's point at infinity.</remarks>
    private static void MarkSp1Infinity(Span<byte> point)
    {
        if (!point.ContainsAnyExcept((byte)0))
            point[0] = Sp1InfinityBit;
    }
}
