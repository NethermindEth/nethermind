// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.TxPool.Filters;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc.TxPool;

/// <summary>
/// Rejects transactions paying less than <see cref="IXdcReleaseSpec.MinimumGasPrice"/>, keeping them out of the pool
/// and out of gossip.
/// </summary>
/// <remarks>
/// Mirrors the two gas price checks of the reference client's <c>validateTx</c>: <c>ErrZeroGasPrice</c> and
/// <c>ErrUnderMinGasPrice</c>, both applied only to transactions that are not special ones - the free block-signing
/// and randomize calls. Neither is a consensus rule, so a block carrying an underpriced transaction is still valid
/// and nothing outside the pool enforces the floor. A zero floor - a gasless subnet - disables both, as
/// <c>common.Gasless</c> does there.
/// <para>
/// Three things differ from what a reader of the neighbouring filters might expect, all of them to follow the
/// reference client:
/// <list type="bullet">
/// <item>Locally submitted transactions are not exempt. The reference exempts them only from its separate
/// <c>pool.gasPrice</c>/base fee check, not from this floor, so neither does this filter - unlike Nethermind's
/// <c>FeeTooLowFilter</c>.</item>
/// <item>The value compared is the fee cap rather than the effective priority fee, because the reference compares
/// <c>tx.GasPrice()</c>, which returns <c>GasFeeCap</c> for a dynamic fee transaction. The reference reserves the
/// tip for replacement bumps and overflow eviction and never gates admission or block building on it. Since XDC's
/// base fee equals the raised floor, comparing the priority fee here would demand twice the price the reference asks
/// for; that is also why <c>Blocks.MinGasPrice</c> cannot carry this value, and why the shipped XDC configs set it
/// to zero.</item>
/// <item>The exemption is <see cref="XdcExtensions.IsSpecialTransaction"/>, narrower than the
/// <see cref="XdcExtensions.RequiresSpecialHandling"/> set that <see cref="XdcTransactionProcessor.BuyGas"/> gives
/// free gas to. The reference keeps XDCX order and lending traffic in its own pools, and both shipped chainspecs
/// disable it outright, so those transactions never reach this one.</item>
/// </list>
/// </para>
/// </remarks>
internal sealed class MinGasPriceFilter(
    IChainHeadInfoProvider chainHeadInfoProvider,
    ISpecProvider specProvider,
    ILogManager logManager) : IIncomingTxFilter
{
    // The reference client's own errors carry no numbers, so these are static and the floor is logged instead.
    private static readonly AcceptTxResult ZeroGasPrice = AcceptTxResult.FeeTooLow.WithMessage("zero gas price");
    private static readonly AcceptTxResult UnderMinGasPrice = AcceptTxResult.FeeTooLow.WithMessage("under min gas price");

    private readonly ILogger _logger = logManager.GetClassLogger<MinGasPriceFilter>();

    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        IXdcReleaseSpec spec = specProvider.GetXdcSpec(chainHeadInfoProvider.HeadNumber + 1);
        UInt256 minimum = spec.MinimumGasPrice;

        if (minimum.IsZero || tx.IsSpecialTransaction(spec))
            return AcceptTxResult.Accepted;

        UInt256 gasPrice = tx.MaxFeePerGas;
        if (gasPrice.IsZero)
            return Reject(tx, ZeroGasPrice, minimum);

        return gasPrice < minimum ? Reject(tx, UnderMinGasPrice, minimum) : AcceptTxResult.Accepted;
    }

    private AcceptTxResult Reject(Transaction tx, AcceptTxResult result, in UInt256 minimum)
    {
        Metrics.PendingTransactionsTooLowFee++;
        if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, {result}, minimum is {minimum}.");
        return result;
    }
}
