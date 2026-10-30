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

/// <summary>Computes the EIP-8141 canonical signature hash <c>keccak(FRAME_TX_TYPE || rlp(tx))</c>, eliding the raw
/// signature bytes of canonical-hash (empty msg) entries. Encodes to a scratch buffer, so the transaction is never mutated.</summary>
// EIP8141-ISSUE: the spec pseudocode for compute_sig_hash mutates tx.signatures in place rather than a copy.
public static class FrameTxSigHash
{
    private const int MaxStackLength = 512;

    // SkipTypedWrapping makes the decoder emit exactly FRAME_TX_TYPE || rlp(tx).
    private const RlpBehaviors Behaviors = RlpBehaviors.SkipTypedWrapping;

    /// <summary>The digest a canonical-hash signature entry of <paramref name="transaction"/> signs.</summary>
    /// <remarks>Allocates; prefer <see cref="ComputeValue"/> on the verification path, which this wraps.</remarks>
    public static Hash256 Compute(Transaction transaction) => new(ComputeValue(transaction));

    /// <summary>The digest a canonical-hash signature entry of <paramref name="transaction"/> signs.</summary>
    /// <remarks>
    /// The preimage elides the <see cref="TxFrameSignature.Signature"/> bytes of canonical-hash entries alone, so
    /// every such entry of one transaction shares this digest and it is computed only once. An entry carrying an
    /// explicit <see cref="TxFrameSignature.Msg"/> signs that digest instead, and its own signature bytes stay in
    /// the preimage (EIP-8141 § Signature Hash), so they must be final before this is computed.
    /// </remarks>
    [SkipLocalsInit]
    public static ValueHash256 ComputeValue(Transaction transaction)
    {
        int length = FrameTxDecoder.GetTransactionLength(transaction, Behaviors, forSigning: true);
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
        RlpWriter writer = RlpWriter.ContentWriter(buffer);
        FrameTxDecoder.EncodeTransaction(transaction, ref writer, Behaviors, forSigning: true);
        Rlp.CheckLength(in writer, buffer.Length);
        return ValueKeccak.Compute(buffer);
    }
}
