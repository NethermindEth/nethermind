// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>A canonical node's structural identity, independent of its in-memory capacity.</summary>
public interface IPbtNodePath<TSelf> : IEquatable<TSelf>, IComparable<TSelf> where TSelf : struct, IPbtNodePath<TSelf>
{
    /// <summary>Gets the maximum supported path depth.</summary>
    static abstract int MaxBitDepth { get; }
    /// <summary>Creates a path from canonical bytes: exactly <c>(bitDepth + 7) / 8</c> of them with unused bits zero.</summary>
    /// <remarks>Only the capacity is checked; the public constructor validates the bytes.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The path exceeds the capacity.</exception>
    static abstract TSelf Create(ReadOnlySpan<byte> path, int bitDepth);
    /// <summary>Gets the number of consumed key bits.</summary>
    int BitDepth { get; }
    /// <summary>Reads a canonical path byte, including zero padding in the final byte.</summary>
    /// <exception cref="IndexOutOfRangeException">The byte index is outside the canonical bytes.</exception>
    byte GetByte(int byteIndex);
    /// <summary>Converts this path to the selected capacity without changing its identity.</summary>
    TPath ToPath<TPath>() where TPath : struct, IPbtNodePath<TPath>;
}
