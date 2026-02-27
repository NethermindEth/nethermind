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
[StructLayout(LayoutKind.Sequential, Pack = 32, Size = 32)]
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
            data.CopyTo(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref _bytes, 1)));
        }
    }

    private static void ThrowInvalidLength() => throw new ArgumentException("Storage value cannot exceed 32 bytes", "data");

    public static StorageValue FromSpanWithoutLeadingZero(ReadOnlySpan<byte> data)
    {
        switch (data.Length)
        {
            case > 32:
                ThrowInvalidLength();
                return default;
            case 32:
                return Unsafe.ReadUnaligned<StorageValue>(ref MemoryMarshal.GetReference(data));
            default:
                Span<byte> buffer = stackalloc byte[32];
                buffer[..(32 - data.Length)].Clear();
                data.CopyTo(buffer[(32 - data.Length)..]);
                return Unsafe.ReadUnaligned<StorageValue>(ref MemoryMarshal.GetReference(buffer));
        }
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

    public override int GetHashCode() => AsReadOnlySpan.FastHash();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(StorageValue left, StorageValue right) => left._bytes == right._bytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(StorageValue left, StorageValue right) => left._bytes != right._bytes;
}
