// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz;

/// <summary>
/// Static-abstract contract implemented by every SSZ-serializable container, forwarding to
/// <c>Nethermind.Serialization.SszEncoding</c>. Allows generic code to invoke decode, encode,
/// and merkleize without having to weave in per-type delegates.
/// </summary>
/// <typeparam name="T">The implementing container type.</typeparam>
public interface ISszCodec<T> where T : ISszCodec<T>
{
    /// <summary>Decodes <paramref name="data"/> into <paramref name="value"/>.</summary>
    static abstract void Decode(ReadOnlySpan<byte> data, out T value);

    /// <summary>Encodes <paramref name="value"/> to a freshly allocated SSZ buffer.</summary>
    static abstract byte[] Encode(T value);

    /// <summary>Computes the SSZ hash-tree root of <paramref name="value"/>.</summary>
    static abstract void Merkleize(T value, out UInt256 root);
}
