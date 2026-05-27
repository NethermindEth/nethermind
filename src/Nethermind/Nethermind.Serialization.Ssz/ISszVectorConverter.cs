// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz;

/// <summary>
/// Marker contract for fixed-length SSZ vector converters.
/// </summary>
/// <remarks>
/// Implementations must also expose a public constant <c>Length</c> member so
/// the SSZ source generator can calculate fixed offsets at generation time.
/// </remarks>
public interface ISszVectorConverter<T>
{
    /// <summary>Decodes a value from its fixed-length SSZ byte vector representation.</summary>
    static abstract T FromSpan(ReadOnlySpan<byte> span);

    /// <summary>Encodes a value into its fixed-length SSZ byte vector representation.</summary>
    static abstract void ToSpan(Span<byte> span, T value);

    /// <summary>Computes the SSZ Merkle root for the fixed-length vector value.</summary>
    static abstract void Merkleize(T value, out UInt256 root);
}
