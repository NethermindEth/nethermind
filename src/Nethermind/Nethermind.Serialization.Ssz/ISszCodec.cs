// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz;

/// <summary>
/// Static-abstract contract implemented by every SSZ-serializable container. Allows generic code
/// to invoke decode, encode, merkleize, and length operations without having to weave in per-type
/// delegates.
/// </summary>
/// <typeparam name="T">The implementing container type.</typeparam>
public interface ISszCodec<T> where T : ISszCodec<T>
{
    /// <summary>Returns the SSZ byte length of <paramref name="value"/>.</summary>
    static abstract int GetLength(T value);

    /// <summary>Returns the total SSZ byte length of <paramref name="items"/>, including the offset table.</summary>
    static abstract int GetLength(ReadOnlySpan<T> items);

    /// <summary>Decodes <paramref name="data"/> into <paramref name="value"/>.</summary>
    static abstract void Decode(ReadOnlySpan<byte> data, out T value);

    /// <summary>Decodes <paramref name="data"/> into <paramref name="values"/> as an array.</summary>
    static abstract void Decode(ReadOnlySpan<byte> data, out T[] values);

    /// <summary>Encodes <paramref name="value"/> to a freshly allocated SSZ buffer.</summary>
    static abstract byte[] Encode(T value);

    /// <summary>Encodes <paramref name="value"/> into <paramref name="data"/>, which must already be sized for the result.</summary>
    static abstract void Encode(Span<byte> data, T value);

    /// <summary>Encodes the collection <paramref name="items"/> to a freshly allocated SSZ buffer.</summary>
    static abstract byte[] Encode(ReadOnlySpan<T> items);

    /// <summary>Encodes the collection <paramref name="items"/> into <paramref name="data"/>, which must already be sized for the result.</summary>
    static abstract void Encode(Span<byte> data, ReadOnlySpan<T> items);

    /// <summary>Computes the SSZ hash-tree root of <paramref name="value"/>.</summary>
    static abstract void Merkleize(T value, out UInt256 root);

    /// <summary>Computes the hash-tree root of <paramref name="items"/> as an SSZ vector.</summary>
    static abstract void MerkleizeVector(ReadOnlySpan<T> items, out UInt256 root);

    /// <summary>Computes the hash-tree root of <paramref name="items"/> as an SSZ list with the given <paramref name="limit"/>.</summary>
    static abstract void MerkleizeList(ReadOnlySpan<T> items, ulong limit, out UInt256 root);

    /// <summary>Computes the hash-tree root of <paramref name="items"/> as an SSZ progressive list (unbounded).</summary>
    static abstract void MerkleizeProgressiveList(ReadOnlySpan<T> items, out UInt256 root);
}
