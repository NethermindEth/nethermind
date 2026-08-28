// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.TxDecoders;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Measures the EIP-8272 reference and EIP-8250 nonce-key calldata an EIP-8141 frame transaction is priced
/// on, for the transactions that did not reach the pool through the RLP decoder.
/// </summary>
/// <remarks>
/// <see cref="FrameTxDecoder"/> measures everything off the wire, but a transaction built field-by-field over
/// <c>eth_sendTransaction</c> never passes through it and would otherwise be priced as if those fields
/// occupied no calldata — under-stating the intrinsic gas, and with it every bound derived from it. Rejects
/// nothing; it exists so the pool prices the same transaction the processor does, which measures for itself
/// before pricing. Must run after <see cref="MalformedTxFilter"/>: measuring encodes into a buffer sized for
/// a well-formed reference list, and before any filter that prices the transaction.
/// </remarks>
internal sealed class FrameTxCalldataStatsFilter : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (!tx.SupportsFrames)
        {
            return AcceptTxResult.Accepted;
        }

        if (tx.NonceKeys is not null)
        {
            tx.FrameCalldataStats = FrameTxNonceCalldata.Measure(tx);
        }

        tx.ReferenceCalldataStats = RecentRootReferenceDecoder.Instance.Measure(tx.RecentRootReferences);
        return AcceptTxResult.Accepted;
    }
}
