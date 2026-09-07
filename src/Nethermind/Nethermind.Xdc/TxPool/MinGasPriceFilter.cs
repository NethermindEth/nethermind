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
/// This is a mempool-admission rule of the reference client (<c>ErrZeroGasPrice</c> and <c>ErrUnderMinGasPrice</c> in
/// its <c>core/txpool/txpool.go</c>), not a consensus rule: a block carrying an underpriced transaction is still
/// valid, so nothing outside the pool enforces the minimum. Special transactions - the free block-signing and
/// randomize calls - are exempt there and here.
/// <para>
/// Two deliberate differences from Nethermind's <c>FeeTooLowFilter</c>, both to follow the reference client:
/// locally submitted transactions are not exempt, and the value compared is the transaction's fee cap rather than its
/// effective priority fee. The minimum is read from the spec of the current head, which is the header the reference
/// client reads it from.
/// </para>
/// </remarks>
internal sealed class MinGasPriceFilter(
    IChainHeadInfoProvider chainHeadInfoProvider,
    ISpecProvider specProvider,
    ILogManager logManager) : IIncomingTxFilter
{
    private readonly ILogger _logger = logManager.GetClassLogger<MinGasPriceFilter>();

    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        IXdcReleaseSpec spec = specProvider.GetXdcSpec(chainHeadInfoProvider.HeadNumber);
        UInt256 minimum = spec.MinimumGasPrice;

        if (minimum.IsZero || tx.IsSpecialTransaction(spec))
            return AcceptTxResult.Accepted;

        // MaxFeePerGas is the fee cap of a 1559 transaction and the gas price of a legacy one, matching what the
        // reference client compares - its tx.GasPrice() returns GasFeeCap for dynamic fee transactions.
        if (tx.MaxFeePerGas < minimum)
        {
            AcceptTxResult result = AcceptTxResult.FeeTooLow.WithMessage($"Gas price below the chain minimum {tx.MaxFeePerGas} < {minimum}");
            if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, {result}.");
            return result;
        }

        return AcceptTxResult.Accepted;
    }
}
