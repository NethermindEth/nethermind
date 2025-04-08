// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.State;

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
    private const int MemorySize = 32;

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
    /// Creates a storage value out of the <paramref name="value"/>.
    /// </summary>
    public StorageValue(in UInt256 value)
    {
        value.ToBigEndian(BytesAsSpan);
    }

    public static implicit operator StorageValue(byte[] bytes) => new(bytes);

    public static readonly StorageValue Zero = default;

    public bool Equals(StorageValue other) => _bytes.Equals(other._bytes);

    public override bool Equals(object? obj)
    {
        return obj is StorageValue other && Equals(other);
    }

    public override int GetHashCode() => Bytes.FastHash();

    private Span<byte> BytesAsSpan => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref Unsafe.AsRef(in _bytes), 1));

    public ReadOnlySpan<byte> Bytes =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _bytes), 1));

    // TODO: optimize potentially to create span only after scanning the vector?
    public ReadOnlySpan<byte> BytesWithNoLeadingZeroes => Bytes.WithoutLeadingZeros();

    public static bool operator ==(StorageValue left, StorageValue right) => left.Equals(right);

    public static bool operator !=(StorageValue left, StorageValue right) => !left.Equals(right);

    public bool IsZero => _bytes == Vector256<byte>.Zero;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string ToHexString(bool withZeroX) => Bytes.WithoutLeadingZeros().ToHexString(withZeroX);

    public override string ToString() => ToHexString(false);

    public byte[] ToArray() => BytesWithNoLeadingZeroes.ToArray();
}
