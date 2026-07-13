// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp.TxDecoders;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// Computes the EIP-8141 canonical signature hash:
/// <c>keccak(FRAME_TX_TYPE || rlp(tx))</c> with the raw signature bytes of canonical-hash
/// (empty msg) entries elided. Pure — the transaction is never mutated.
/// </summary>
// EIP8141-ISSUE: the spec pseudocode for compute_sig_hash mutates tx.signatures in place —
// implementations following it literally corrupt the transaction; the spec should state it
// operates on a copy. This implementation streams the elided form directly into the hasher.
public static class FrameTxSigHash
{
    private static readonly FrameTxDecoder<Transaction> Decoder = new();

    public static Hash256 Compute(Transaction transaction) => new(ComputeValue(transaction));

    public static ValueHash256 ComputeValue(Transaction transaction)
    {
        KeccakRlpWriter writer = new();
        WriteTypedForSigning(ref writer, transaction);
        return writer.GetValueHash();
    }

    private static void WriteTypedForSigning<TWriter>(ref TWriter writer, Transaction transaction)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.WriteByte((byte)TxType.FrameTx);
        Decoder.Encode(transaction, ref writer, forSigning: true);
    }
}
