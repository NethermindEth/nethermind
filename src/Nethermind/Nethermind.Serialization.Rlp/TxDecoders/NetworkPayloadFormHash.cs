// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp.TxDecoders;

/// <summary>Hashes the inner payload of a transaction that travels the network inside a wrapper.</summary>
/// <remarks>Shared by the blob and frame decoders, which each held a byte-identical private copy.</remarks>
internal static class NetworkPayloadFormHash
{
    /// <summary>Computes <c>keccak(txType || transactionSequence)</c>, the canonical hash of the wrapped
    /// transaction rather than of the wrapper carrying it.</summary>
    /// <param name="txType">The transaction's envelope type byte.</param>
    /// <param name="transactionSequence">The inner payload sequence, without that type byte.</param>
    /// <remarks>The input is joined in a stack or pooled buffer and hashed in one call, so no hash state is allocated.</remarks>
    [SkipLocalsInit]
    public static Hash256 Calculate(TxType txType, ReadOnlySpan<byte> transactionSequence)
    {
        const int MaxStackLength = 1024;
        int length = 1 + transactionSequence.Length;
        byte[]? rented = null;
        Span<byte> input = length <= MaxStackLength
            ? stackalloc byte[MaxStackLength]
            : rented = ArrayPool<byte>.Shared.Rent(length);
        input[0] = (byte)txType;
        transactionSequence.CopyTo(input[1..]);
        Hash256 hash = new(ValueKeccak.Compute(input[..length]));
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        return hash;
    }
}
