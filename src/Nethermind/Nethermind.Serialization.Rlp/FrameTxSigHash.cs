// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp.TxDecoders;

namespace Nethermind.Serialization.Rlp;

/// <summary>Computes the EIP-8141 canonical signature hash <c>keccak(FRAME_TX_TYPE || rlp(tx))</c>, eliding the raw
/// signature bytes of canonical-hash (empty msg) entries. Streams into the hasher, so the transaction is never mutated.</summary>
// EIP8141-ISSUE: the spec pseudocode for compute_sig_hash mutates tx.signatures in place rather than a copy.
public static class FrameTxSigHash
{
    private static readonly FrameTxDecoder<Transaction> Decoder = new();

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
    public static ValueHash256 ComputeValue(Transaction transaction)
    {
        KeccakRlpWriter writer = new();
        WriteTypedForSigning(ref writer, transaction);
        return writer.GetValueHash();
    }

    // SkipTypedWrapping makes the decoder emit exactly FRAME_TX_TYPE || rlp(tx).
    private static void WriteTypedForSigning<TWriter>(ref TWriter writer, Transaction transaction)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
        => Decoder.Encode(transaction, ref writer, RlpBehaviors.SkipTypedWrapping, forSigning: true);
}
