// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm.Precompiles;

internal static partial class Eip2537
{
    /// <param name="source">Must have 96 bytes length.</param>
    /// <param name="destination">Must be zero-initialized.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EncodeG1(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        source[..48].CopyTo(destination[16..64]);
        source[48..].CopyTo(destination[80..128]);
    }

    /// <param name="source">Must have 192 bytes length.</param>
    /// <param name="destination">Must be zero-initialized.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EncodeG2(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        source[..48].CopyTo(destination[16..64]);
        source[48..96].CopyTo(destination[80..128]);
        source[96..144].CopyTo(destination[144..192]);
        source[144..].CopyTo(destination[208..256]);
    }

    /// <param name="source">Must have 64 bytes length.</param>
    /// <param name="destination">Must have 48 bytes length.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryDecodeFp(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source[..16].ContainsAnyExcept((byte)0))
            return false;

        source[16..].CopyTo(destination);

        return true;
    }

    /// <param name="source">Must have 128 bytes length.</param>
    /// <param name="destination">Must have 96 bytes length.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryDecodeFp2(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source[..16].ContainsAnyExcept((byte)0) || source[64..80].ContainsAnyExcept((byte)0))
            return false;

        source[16..64].CopyTo(destination);
        source[80..128].CopyTo(destination[48..]);

        return true;
    }

    /// <param name="source">Must have 128 bytes length.</param>
    /// <param name="destination">Must have 96 bytes length.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryDecodeG1(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source[..16].ContainsAnyExcept((byte)0) || source[64..80].ContainsAnyExcept((byte)0))
            return false;

        source[16..64].CopyTo(destination);
        source[80..128].CopyTo(destination[48..]);

        return true;
    }

    /// <param name="source">Must have 256 bytes length.</param>
    /// <param name="destination">Must have 192 bytes length.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryDecodeG2(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source[..16].ContainsAnyExcept((byte)0) || source[64..80].ContainsAnyExcept((byte)0))
            return false;

        source[16..64].CopyTo(destination);
        source[80..128].CopyTo(destination[48..96]);
        source[144..192].CopyTo(destination[96..144]);
        source[208..256].CopyTo(destination[144..]);

        return true;
    }
}
