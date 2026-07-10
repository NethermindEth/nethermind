// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Extensions;

namespace Nethermind.State.Flat;

/// <summary>
/// Make storing slot value smaller than a byte[].
/// </summary>
/// <remarks>Wraps a full 32-byte word verbatim (both are big-endian <c>Vector256&lt;byte&gt;</c>).</remarks>
[StructLayout(LayoutKind.Sequential, Pack = 32, Size = 32)]
public readonly struct SlotValue(in EvmWord word)
{
    public readonly Vector256<byte> _bytes = word; // Use Vector256 as the internal storage field
    public Span<byte> AsSpan => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref Unsafe.AsRef(in _bytes), 1));
    public ReadOnlySpan<byte> AsReadOnlySpan => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _bytes), 1));
    public const int ByteCount = 32;

    /// <summary>The value as the <c>EvmWord</c> the storage layer carries. Zero-cost — same underlying representation.</summary>
    public EvmWord AsWord => _bytes;

    /// <summary>True when the slot holds zero.</summary>
    public bool IsZero => _bytes == default;

    private static void ThrowInvalidLength() => throw new ArgumentException("Slot value cannot exceed 32 bytes", "data");

    /// <summary>
    /// Builds a slot value from big-endian bytes, right-aligning shorter input by padding leading zeros.
    /// </summary>
    /// <remarks>
    /// The only way to construct a non-zero value: slot values are big-endian 32-byte words, so a shorter
    /// buffer must be padded at the front. A left-aligning overload would silently scale the value by a
    /// power of 256.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <paramref name="data"/> exceeds 32 bytes.</exception>
    public static SlotValue FromSpanWithoutLeadingZero(ReadOnlySpan<byte> data)
    {
        switch (data.Length)
        {
            case > 32:
                ThrowInvalidLength();
                return default;
            case 32:
                return Unsafe.ReadUnaligned<SlotValue>(ref MemoryMarshal.GetReference(data));
            default:
                Span<byte> buffer = stackalloc byte[32];
                buffer[..(32 - data.Length)].Clear();
                data.CopyTo(buffer[(32 - data.Length)..]);
                return Unsafe.ReadUnaligned<SlotValue>(ref MemoryMarshal.GetReference(buffer));
        }
    }

    /// <summary>
    /// Currently, the worldstate that the evm use expect the bytes to be without leading zeros
    /// </summary>
    private static readonly byte[] ZeroBytes = [0];

    public byte[] ToEvmBytes()
    {
        if (_bytes == Vector256<byte>.Zero) return ZeroBytes;
        return AsReadOnlySpan.WithoutLeadingZeros().ToArray();
    }
}
