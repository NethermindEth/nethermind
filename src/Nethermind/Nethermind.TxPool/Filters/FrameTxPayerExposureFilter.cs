// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters;

/// <summary>Rejects a frame transaction whose payer's summed pending maximum cost would exceed its balance (EIP-8141).</summary>
/// <remarks>The reservation is taken at admission and released when the transaction leaves the pool.</remarks>
internal sealed class FrameTxPayerExposureFilter(
    IChainHeadSpecProvider specProvider,
    IReadOnlyStateProvider stateProvider,
    TxDistinctSortedPool standardPool,
    TxDistinctSortedPool blobPool,
    PayerExposureCache exposure,
    ILogger logger) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        Address? payer = tx.SupportsFrames ? tx.PayerAddress : null;
        if (payer is null)
        {
            return AcceptTxResult.Accepted;
        }

        // The upper-bound TXPARAM(0x06), priced with the processor's helper so the admission bound
        // and the payer-solvency gate cannot drift.
        IReleaseSpec spec = specProvider.GetCurrentHeadSpec();
        if (!FrameTxValidation.TryCalculateMaxCost(tx, spec, out UInt256 maxCost))
        {
            // Unincludable rather than malformed: Invalid is the one result that disconnects the relaying peer.
            return AcceptTxResult.Int256Overflow.WithMessage("Frame transaction maximum cost cannot be priced");
        }

        // A simulated third-party payer must be read from state, or the bound gates the wrong account.
        UInt256 balance = payer == tx.SenderAddress
            ? state.SenderAccount.Balance
            : stateProvider.TryGetAccount(payer, out AccountStruct payerAccount) ? payerAccount.Balance : UInt256.Zero;

        // AddCore settles the replacement later. The discount is ignored with no reservation held, so skip the walk.
        Hash256? replaced = exposure.GetReserved(payer).IsZero ? null : PendingReplacement.Find(tx, standardPool, blobPool)?.Hash;
        if (!exposure.TryReserve(payer, tx.Hash!, maxCost, balance, out UInt256 reserved, replaced))
        {
            // Concurrent submissions share the pool's head read lock, so the ledger is what serialises them.
            Interlocked.Increment(ref Metrics.PendingTransactionsFrameTxPayerExposureExceeded);
            if (logger.IsTrace)
                logger.Trace($"Skipped adding frame transaction {tx.Hash}, payer {payer} reserved exposure {reserved} + {maxCost} exceeds balance {balance}.");
            return AcceptTxResult.FrameTxPayerExposureExceeded;
        }

        // Recorded for the restart-time restore; the ledger owns what a removal releases.
        tx.PayerExposure = maxCost;
        return AcceptTxResult.Accepted;
    }
}
