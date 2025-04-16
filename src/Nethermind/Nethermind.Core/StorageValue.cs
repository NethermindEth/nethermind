// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core;

/// <summary>
/// Represents a storage value.
/// </summary>
/// <remarks>
/// The storage value keeps the value as a 32-byte vector.
/// This might introduce some memory overhead for small values, but should greatly increase locality
/// and reduce littering with small byte[] arrays.
/// </remarks>
public readonly struct StorageValue : IEquatable<StorageValue>
{
    private readonly Vector256<byte> _bytes;
    public const int MemorySize = 32;

    /// <summary>
    /// Creates a new storage value, ensuring proper endianess of the copied bytes.
    /// </summary>
    public StorageValue(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == MemorySize)
        {
            _bytes = Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(bytes));
        }
        else
        {
            _bytes = default;
            bytes.CopyTo(BytesAsSpan.Slice(MemorySize - bytes.Length));
        }
    }

    /// <summary>
    /// Creates a storage value from raw vector.
    /// </summary>
    public StorageValue(in Vector256<byte> bytes)
    {
        _bytes = bytes;
    }

    /// <summary>
    /// Creates a storage value out of the <paramref name="value"/>.
    /// </summary>
    public StorageValue(in UInt256 value)
    {
        value.ToBigEndian(BytesAsSpan);
    }


    /// <summary>
    /// Transforms this storage value into the <see cref="UInt256"/> big endian.
    /// </summary>
    public UInt256 BigEndianUInt
    {
        [method: MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(BytesAsSpan, true);
    }

    public static implicit operator StorageValue(byte[] bytes) => new(bytes);

    public static readonly StorageValue Zero = default;

    [OverloadResolutionPriority(1)]
    public bool Equals(in StorageValue other) => _bytes.Equals(other._bytes);

    public bool Equals(StorageValue other) => _bytes.Equals(other._bytes);

    public override bool Equals(object? obj)
    {
        return obj is StorageValue other && Equals(other);
    }

    public override int GetHashCode()
    {
        var b = Unsafe.As<Vector256<byte>, byte>(ref Unsafe.AsRef(in _bytes));

        uint hash = 13;

        uint hash0 = BitOperations.Crc32C(hash, Unsafe.ReadUnaligned<ulong>(ref b));
        uint hash1 = BitOperations.Crc32C(hash, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, sizeof(ulong))));
        uint hash2 = BitOperations.Crc32C(hash, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, sizeof(ulong) * 2)));

        // Omit the highest that are likely 00000.

        return (unchecked((int)BitOperations.Crc32C(hash1, ((ulong)hash0 << (sizeof(uint) * 8)) | hash2)));
    }

    private Span<byte> BytesAsSpan => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref Unsafe.AsRef(in _bytes), 1));

    public ReadOnlySpan<byte> Bytes =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _bytes), 1));

    /// <summary>
    /// Provides unsafe direct access to the vector reference.
    /// </summary>
    public ref readonly Vector256<byte> UnsafeRef => ref Unsafe.AsRef(in _bytes);

    public ReadOnlySpan<byte> BytesWithNoLeadingZeroes
    {
        get
        {
            if (Vector256.IsHardwareAccelerated)
            {
                if (_bytes == Vector256<byte>.Zero)
                    return Nethermind.Core.Extensions.Bytes.ZeroByteSpan;

                // At least one byte is set.
                // To get the number of leading zeroes, check which are equal to zero and negate.

                var setBytes =
                    ~
                        Vector256.Equals(Vector256<byte>.Zero, _bytes)
                            .ExtractMostSignificantBits();

                // It's one bit per byte, count trailing zero bits then.
                var offset = BitOperations.TrailingZeroCount(setBytes);

                return MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.Add(ref Unsafe.As<Vector256<byte>, byte>(ref Unsafe.AsRef(in _bytes)), offset),
                    MemorySize - offset);
            }


            return Bytes.WithoutLeadingZeros();
        }
    }

    public static bool operator ==(StorageValue left, StorageValue right) => left.Equals(right);

    public static bool operator !=(StorageValue left, StorageValue right) => !left.Equals(right);

    public bool IsZero => _bytes == Vector256<byte>.Zero;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string ToHexString(bool withZeroX) => BytesWithNoLeadingZeroes.ToHexString(withZeroX);

    public override string ToString() => ToHexString(false);

    public byte[] ToArray() => BytesWithNoLeadingZeroes.ToArray();

    public static StorageValue FromHexString(ReadOnlySpan<byte> hex)
    {
        const int maxChars = MemorySize * 2;

        StorageValue result = default;

        if (hex.Length < maxChars)
        {
            Span<byte> hex32 = stackalloc byte[maxChars];
            hex32.Fill((byte)'0');
            hex.CopyTo(hex32[(64 - hex.Length)..]);

            Nethermind.Core.Extensions.Bytes.FromUtf8HexString(hex32, result.BytesAsSpan);
        }
        else
        {
            Nethermind.Core.Extensions.Bytes.FromUtf8HexString(hex, result.BytesAsSpan);
        }

        return result;
    }
}
