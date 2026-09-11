// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    public static Hash256 Calculate(TxType txType, ReadOnlySpan<byte> transactionSequence)
    {
        KeccakHash hash = KeccakHash.Create();
        Span<byte> typeByte = [(byte)txType];
        hash.Update(typeByte);
        hash.Update(transactionSequence);
        return new Hash256(hash.GenerateValueHash());
    }
}
