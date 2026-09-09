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
/// before pricing. Must run before every filter that prices a frame transaction, <see cref="GasLimitTxFilter"/>
/// and <see cref="MalformedTxFilter"/> included, or admission and head revalidation price different transactions.
/// An over-long set is left unmeasured rather than measured into an out-of-range buffer; <see cref="MalformedTxFilter"/>
/// rejects it for the same bound further down the pipeline.
/// </remarks>
internal sealed class FrameTxCalldataStatsFilter : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (!tx.SupportsFrames)
        {
            return AcceptTxResult.Accepted;
        }

        if (tx.NonceKeys is { Length: <= Eip8250Constants.MaxNonceKeys })
        {
            tx.FrameCalldataStats = FrameTxNonceCalldata.Measure(tx);
        }

        if (tx.RecentRootReferences is null or { Length: <= Eip8272Constants.MaxRecentRootReferences })
        {
            tx.ReferenceCalldataStats = RecentRootReferenceDecoder.Instance.Measure(tx.RecentRootReferences);
        }

        return AcceptTxResult.Accepted;
    }
}
