// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Extensions;

namespace Nethermind.State.Pbt;

/// <summary>
/// Conversions between the EVM's stripped (leading-zeros-removed) storage-value byte arrays and the
/// fixed 32-byte <c>EvmWord</c> (<see cref="Vector256{Byte}"/>) used to hold slot values inline.
/// </summary>
public static class EvmWordSlot
{
    /// <summary>Left-pads a stripped value (0..32 bytes, big-endian) into a 32-byte word.</summary>
    public static EvmWord FromStripped(ReadOnlySpan<byte> stripped)
    {
        if (stripped.Length == 32) return Unsafe.ReadUnaligned<EvmWord>(ref MemoryMarshal.GetReference(stripped));

        EvmWord word = default;
        stripped.CopyTo(AsSpan(ref word)[(32 - stripped.Length)..]);
        return word;
    }

    public static bool IsZero(in EvmWord word) => word == default;

    /// <summary>A 32-byte view over the word. Only valid over an lvalue (local/field), never a temporary.</summary>
    public static ReadOnlySpan<byte> AsReadOnlySpan(in EvmWord word) =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in word), 1));

    private static Span<byte> AsSpan(ref EvmWord word) =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref word, 1));

    /// <summary>The stripped (leading-zeros-removed) representation the EVM world state expects; empty for zero.</summary>
    public static byte[] ToStrippedBytes(in EvmWord word) => AsReadOnlySpan(in word).WithoutLeadingZeros().ToArray();

    /// <summary>The full 32-byte representation, e.g. to feed a tree leaf.</summary>
    public static byte[] ToArray32(in EvmWord word) => AsReadOnlySpan(in word).ToArray();
}
