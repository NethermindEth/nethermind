// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp.TxDecoders;

namespace Nethermind.Serialization.Rlp;

/// <summary>Computes the EIP-8141 canonical signature hash <c>keccak(FRAME_TX_TYPE || 0x00 || rlp(tx_body))</c>.</summary>
/// <remarks>The <c>0x00</c> separates it from the transaction hash, <c>keccak(FRAME_TX_TYPE || rlp(tx_body))</c>, so an
/// off-chain signature over a transaction hash cannot authorize a transaction. The body carries no witness bytes, so
/// nothing is elided and every claim with an empty <c>msg</c> shares the digest.</remarks>
public static class FrameTxSigHash
{
    private const int MaxStackLength = 512;
    private const int PrefixLength = 2;
    private const byte DomainSeparator = 0x00;

    /// <summary>The digest a canonical-hash claim of <paramref name="transaction"/> is over.</summary>
    /// <remarks>Allocates; prefer <see cref="ComputeValue"/> on the verification path, which this wraps.</remarks>
    public static Hash256 Compute(Transaction transaction) => new(ComputeValue(transaction));

    /// <summary>The digest a canonical-hash claim of <paramref name="transaction"/> is over.</summary>
    [SkipLocalsInit]
    public static ValueHash256 ComputeValue(Transaction transaction)
    {
        int length = PrefixLength + Rlp.LengthOfSequence(FrameTxDecoder.GetContentLength(transaction));
        if (length <= MaxStackLength)
        {
            Span<byte> buffer = stackalloc byte[MaxStackLength];
            return EncodeAndHash(transaction, buffer[..length]);
        }

        using ArrayPoolSpan<byte> pooled = new(length);
        return EncodeAndHash(transaction, pooled);
    }

    private static ValueHash256 EncodeAndHash(Transaction transaction, Span<byte> buffer)
    {
        buffer[0] = (byte)TxType.FrameTx;
        buffer[1] = DomainSeparator;
        RlpWriter writer = RlpWriter.ContentWriter(buffer[PrefixLength..]);
        RlpSequence sequence = writer.StartSequence(FrameTxDecoder.GetContentLength(transaction));
        FrameTxDecoder.EncodePayload(transaction, ref writer);
        writer.EndSequence(sequence);
        Rlp.CheckLength(in writer, buffer.Length - PrefixLength);
        return ValueKeccak.Compute(buffer);
    }
}
