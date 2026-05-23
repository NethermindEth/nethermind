// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

/// <summary>Wraps bytes represented as a 0x-prefixed hex JSON string.</summary>
public readonly struct HexBytes : IEquatable<HexBytes>
{
    /// <summary>The bytes to write as hex.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>Initializes a new instance from an owned or externally stable byte array.</summary>
    public HexBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Bytes = bytes;
    }

    /// <summary>Initializes a new instance from an owned or externally stable memory region.</summary>
    public HexBytes(ReadOnlyMemory<byte> bytes) => Bytes = bytes;

    /// <inheritdoc/>
    public bool Equals(HexBytes other) => Bytes.Span.SequenceEqual(other.Bytes.Span);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is HexBytes other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hashCode = new();
        hashCode.AddBytes(Bytes.Span);
        return hashCode.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() => "0x" + Convert.ToHexStringLower(Bytes.Span);

}
