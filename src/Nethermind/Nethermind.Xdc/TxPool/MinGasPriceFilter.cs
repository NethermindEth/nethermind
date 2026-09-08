// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.TxPool.Filters;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc.TxPool;

/// <summary>
/// Rejects transactions paying less than <see cref="XdcConstants.MinGasPrice"/>, keeping them out of the pool and out
/// of gossip.
/// </summary>
/// <remarks>
/// This is a mempool-admission rule of the reference client (<c>ErrZeroGasPrice</c> and <c>ErrUnderMinGasPrice</c> in
/// its <c>core/txpool/txpool.go</c>), not a consensus rule: a block carrying an underpriced transaction is still
/// valid, so nothing outside the pool enforces the floor. Special transactions - the free block-signing and randomize
/// calls - are exempt there and here.
/// <para>
/// Three things differ from what a reader of the neighbouring filters might expect, all of them to follow the
/// reference client:
/// <list type="bullet">
/// <item>Locally submitted transactions are not exempt. The reference exempts them only from its separate
/// <c>pool.gasPrice</c>/base fee check, not from this floor, so neither does this filter - unlike Nethermind's
/// <c>FeeTooLowFilter</c>.</item>
/// <item>The value compared is the fee cap rather than the effective priority fee, because the reference compares
/// <c>tx.GasPrice()</c>, which returns <c>GasFeeCap</c> for a dynamic fee transaction. XDC's base fee is a constant
/// equal to this floor, so comparing the priority fee would demand twice the price the reference asks for. That is
/// also why <c>Blocks.MinGasPrice</c> cannot carry this value - it drives the block production filter off the
/// priority fee, and the shipped XDC configs set it to zero for that reason.</item>
/// <item>The exemption is <see cref="XdcExtensions.IsSpecialTransaction"/>, narrower than the
/// <see cref="XdcExtensions.RequiresSpecialHandling"/> set that <see cref="XdcTransactionProcessor.BuyGas"/> gives
/// free gas to. The reference keeps XDCX order and lending traffic in its own pools, so those transactions never
/// reach this one.</item>
/// </list>
/// </para>
/// </remarks>
internal sealed class MinGasPriceFilter(
    IChainHeadInfoProvider chainHeadInfoProvider,
    ISpecProvider specProvider,
    ILogManager logManager) : IIncomingTxFilter
{
    // Built once: the floor is a constant, and the rejection path is reachable by any peer flooding underpriced
    // transactions.
    private static readonly AcceptTxResult Underpriced =
        AcceptTxResult.FeeTooLow.WithMessage($"Gas price below the {XdcConstants.MinGasPrice} wei chain minimum");

    private readonly ILogger _logger = logManager.GetClassLogger<MinGasPriceFilter>();

    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        // The spec is only consulted for the special transaction addresses, so the block it is read for does not
        // matter; head + 1 matches the sibling XDC filters.
        IXdcReleaseSpec spec = specProvider.GetXdcSpec(chainHeadInfoProvider.HeadNumber + 1);

        if (tx.IsSpecialTransaction(spec) || tx.MaxFeePerGas >= XdcConstants.MinGasPrice)
            return AcceptTxResult.Accepted;

        Metrics.PendingTransactionsTooLowFee++;
        if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, {Underpriced}.");
        return Underpriced;
    }
}
