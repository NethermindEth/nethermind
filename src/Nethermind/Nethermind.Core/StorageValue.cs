// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Extensions;

namespace Nethermind.Core;

/// <summary>
/// Inline 32-byte struct for storage slot values, replacing heap-allocated byte[].
/// Uses Vector256&lt;byte&gt; for efficient SIMD comparison and zero-check.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 32)]
public readonly struct StorageValue : IEquatable<StorageValue>
{
    public static readonly StorageValue Zero = default;
    private static readonly byte[] ZeroByte = [0];

    public readonly Vector256<byte> _bytes;

    public Span<byte> AsSpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref Unsafe.AsRef(in _bytes), 1));
    }

    public ReadOnlySpan<byte> AsReadOnlySpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _bytes), 1));
    }

    public const int ByteCount = 32;

    public bool IsZero
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _bytes == Vector256<byte>.Zero;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StorageValue(ReadOnlySpan<byte> data)
    {
        if (data.Length > 32)
        {
            ThrowInvalidLength();
        }

        if (data.Length == 32)
        {
            _bytes = Unsafe.ReadUnaligned<Vector256<byte>>(ref MemoryMarshal.GetReference(data));
        }
        else
        {
            _bytes = Vector256<byte>.Zero;
            Span<byte> span = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref _bytes, 1));
            data.CopyTo(span[(ByteCount - data.Length)..]);
        }
    }

    private static void ThrowInvalidLength() => throw new ArgumentException("Storage value cannot exceed 32 bytes", "data");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StorageValue FromSpanWithoutLeadingZero(ReadOnlySpan<byte> data)
    {
        if (data.Length == 32)
            return Unsafe.ReadUnaligned<StorageValue>(ref MemoryMarshal.GetReference(data));
        return FromSpanWithoutLeadingZeroSlow(data);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static StorageValue FromSpanWithoutLeadingZeroSlow(ReadOnlySpan<byte> data)
    {
        if (data.Length > 32) { ThrowInvalidLength(); return default; }
        Span<byte> buffer = stackalloc byte[32];
        buffer[..(32 - data.Length)].Clear();
        data.CopyTo(buffer[(32 - data.Length)..]);
        return Unsafe.ReadUnaligned<StorageValue>(ref MemoryMarshal.GetReference(buffer));
    }

    public static StorageValue? FromBytes(byte[]? data) => data is null ? null : new StorageValue(data);

    /// <summary>
    /// Returns the value as byte[] without leading zeros, matching the EVM storage convention.
    /// Zero values are returned as [0].
    /// </summary>
    public byte[] ToEvmBytes()
    {
        if (IsZero) return ZeroByte;
        return AsReadOnlySpan.WithoutLeadingZeros().ToArray();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(StorageValue other) => _bytes == other._bytes;

    public override bool Equals(object? obj) => obj is StorageValue other && Equals(other);

    public override int GetHashCode() =>
        (int)SpanExtensions.FastHash64For32Bytes(
            ref Unsafe.As<Vector256<byte>, byte>(ref Unsafe.AsRef(in _bytes)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Equals(in StorageValue left, in StorageValue right) => left._bytes == right._bytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool NotEquals(in StorageValue left, in StorageValue right) => left._bytes != right._bytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(StorageValue left, StorageValue right) => left._bytes == right._bytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(StorageValue left, StorageValue right) => left._bytes != right._bytes;
}
