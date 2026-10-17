// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db;

/// <summary>Decodes an owned result from a borrowed database value.</summary>
public interface IValueDecoder<T>
{
    /// <summary>Decodes a present value, including an empty value. The buffer must not escape this call.</summary>
    T Decode(ReadOnlySpan<byte> buffer);
}

/// <summary>Reads fixed-length keys in a batch without first allocating value byte arrays.</summary>
public interface IBatchReadOnlyDb
{
    /// <summary>Decodes values in key order into the supplied output. Missing keys produce default values
    /// without calling the decoder. Keys must contain exactly one fixed-length key per output element.</summary>
    /// <remarks>The key and output buffers must not overlap. A decoder failure preserves the decoded prefix
    /// and leaves the remaining output cleared.</remarks>
    void GetMany<T, TDecoder>(ReadOnlySpan<byte> keys, int keyLength, Span<T> values, TDecoder decoder)
        where TDecoder : IValueDecoder<T>;
}
