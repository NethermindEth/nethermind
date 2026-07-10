// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Evm.State;

/// <summary>
/// Conversions between the fixed-width <c>EvmWord</c> that carries storage values in memory and the
/// minimal-length big-endian encoding used at the trie/RLP boundary and on the legacy
/// <see cref="IWorldState.Get"/>/<see cref="IWorldState.Set"/> surface.
/// </summary>
public static class StorageWord
{
    /// <summary>Returns the full 32-byte big-endian view of <paramref name="word"/>. The span aliases <paramref name="word"/>.</summary>
    public static ReadOnlySpan<byte> AsSpan(in EvmWord word) =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in word), 1));

    /// <summary>Returns the storage encoding of <paramref name="word"/>: leading zeros stripped, or <c>[0]</c> when zero.</summary>
    /// <remarks>The returned span aliases <paramref name="word"/> unless it is zero.</remarks>
    public static ReadOnlySpan<byte> ToStorageBytes(in EvmWord word, out bool isZero)
    {
        ReadOnlySpan<byte> bytes = AsSpan(in word);
        isZero = bytes.IsZero();
        return isZero ? VirtualMachineStatics.BytesZero : bytes.WithoutLeadingZeros();
    }

    /// <summary>Widens a minimal-length big-endian storage value back into a full word, padding leading zeros.</summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="bytes"/> exceeds 32 bytes.</exception>
    public static EvmWord FromStorageBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > EvmWordSize) ThrowValueTooLong(bytes.Length);

        EvmWord word = default;
        bytes.CopyTo(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref word, 1))[(EvmWordSize - bytes.Length)..]);
        return word;
    }

    private const int EvmWordSize = 32;

    private static void ThrowValueTooLong(int length) =>
        throw new ArgumentException($"Storage value cannot exceed {EvmWordSize} bytes, was {length}", "bytes");
}

/// <summary>
/// The comparisons that SSTORE net gas metering needs from a storage write, as reported by
/// <see cref="IWorldState.SStore"/>.
/// </summary>
/// <remarks>
/// SSTORE never reads the stored digits: EIP-2200 and EIP-3529 are defined purely in terms of whether the
/// original, current and new values are zero and whether they are equal. Reporting the answers rather than
/// the bytes keeps the value encoding private to the backend.
/// <para>
/// <see cref="CurrentSameAsOriginal"/>, <see cref="OriginalIsZero"/> and <see cref="NewSameAsOriginal"/> are
/// only meaningful when <see cref="NewSameAsCurrent"/> is unset. A store that changes nothing must not read
/// the original value, because witness and access-list tracing observe that read.
/// </para>
/// </remarks>
[Flags]
public enum SStoreState : byte
{
    None = 0,

    /// <summary>The new value equals the value already stored, so no write was performed.</summary>
    NewSameAsCurrent = 1 << 0,

    /// <summary>The value stored before this write is zero.</summary>
    CurrentIsZero = 1 << 1,

    /// <summary>The value stored before this write is the value the cell held at the start of the transaction.</summary>
    CurrentSameAsOriginal = 1 << 2,

    /// <summary>The value the cell held at the start of the transaction is zero.</summary>
    OriginalIsZero = 1 << 3,

    /// <summary>The new value restores the cell to the value it held at the start of the transaction.</summary>
    NewSameAsOriginal = 1 << 4,
}
