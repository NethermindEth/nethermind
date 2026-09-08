// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>A canonical node's structural identity, independent of its in-memory capacity.</summary>
public interface IPbtNodePath : IEquatable<IPbtNodePath>, IComparable<IPbtNodePath>
{
    /// <summary>Gets the number of consumed key bits.</summary>
    int BitDepth { get; }
    /// <summary>Gets the canonical zero-padded path bytes.</summary>
    ReadOnlySpan<byte> Path { get; }
    /// <summary>Encodes the depth and canonical path bytes.</summary>
    byte[] Encode();
}

/// <summary>Constructs paths with a statically selected inline capacity.</summary>
public interface IPbtNodePath<TSelf> : IPbtNodePath where TSelf : class, IPbtNodePath<TSelf>
{
    /// <summary>Gets the maximum supported path depth.</summary>
    static abstract int MaxBitDepth { get; }
    /// <summary>Creates a path, validating its length and unused bits.</summary>
    static abstract TSelf Create(ReadOnlySpan<byte> path, int bitDepth);
}
