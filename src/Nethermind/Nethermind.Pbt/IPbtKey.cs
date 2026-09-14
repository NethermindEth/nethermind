// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Pbt;

/// <summary>A complete inline tree key supporting allocation-free specialized mutation processing.</summary>
public interface IPbtKey<TSelf> : IEquatable<TSelf>, IComparable<TSelf> where TSelf : struct, IPbtKey<TSelf>
{
    /// <summary>Gets whether all valid keys have the same logical length.</summary>
    static virtual bool IsFixedLength => false;
    /// <summary>Gets the maximum supported byte length.</summary>
    static abstract int Capacity { get; }
    /// <summary>Creates a key from its exact bytes.</summary>
    static abstract TSelf Create(ReadOnlySpan<byte> bytes);
    /// <summary>Gets the complete key bytes.</summary>
    [UnscopedRef]
    ReadOnlySpan<byte> Bytes { get; }
    /// <summary>Gets the byte length.</summary>
    int Length { get; }
    /// <summary>Gets the bit length.</summary>
    int BitLength { get; }
    /// <summary>Reads one MSB-first bit.</summary>
    int GetBit(int bitIndex);
    /// <summary>Tests whether this key prefixes another key.</summary>
    bool IsPrefixOf(TSelf other);
    /// <summary>Finds the first different bit, or the common bit length.</summary>
    int FirstDifferingBit(TSelf other, int startBit = 0);
}
